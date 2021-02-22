﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeStyle;
using Microsoft.CodeAnalysis.CSharp.CodeStyle;
using Microsoft.CodeAnalysis.CSharp.Diagnostics.TypeStyle;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Diagnostics.EngineV2;
using Microsoft.CodeAnalysis.Editor.UnitTests;
using Microsoft.CodeAnalysis.Editor.UnitTests.Workspaces;
using Microsoft.CodeAnalysis.Remote;
using Microsoft.CodeAnalysis.Remote.Testing;
using Microsoft.CodeAnalysis.Serialization;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.SolutionCrawler;
using Microsoft.CodeAnalysis.Test.Utilities;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic.UseNullPropagation;
using Microsoft.CodeAnalysis.Workspaces.Diagnostics;
using Roslyn.Test.Utilities;
using Roslyn.Utilities;
using Xunit;

namespace Roslyn.VisualStudio.Next.UnitTests.Remote
{
    [UseExportProvider]
    public class VisualStudioDiagnosticAnalyzerExecutorTests
    {
        [Fact, Trait(Traits.Feature, Traits.Features.RemoteHost)]
        public async Task TestCSharpAnalyzerOptions()
        {
            var code = @"class Test
{
    void Method()
    {
        var t = new Test();
    }
}";

            using (var workspace = CreateWorkspace(LanguageNames.CSharp, code))
            {
                var analyzerType = typeof(CSharpUseExplicitTypeDiagnosticAnalyzer);
                var analyzerResult = await AnalyzeAsync(workspace, workspace.CurrentSolution.ProjectIds.First(), analyzerType);

                var diagnostics = analyzerResult.GetDocumentDiagnostics(analyzerResult.DocumentIds.First(), AnalysisKind.Semantic);
                Assert.Equal(IDEDiagnosticIds.UseExplicitTypeDiagnosticId, diagnostics[0].Id);
                Assert.Equal(DiagnosticSeverity.Hidden, diagnostics[0].Severity);

                // set option
                workspace.TryApplyChanges(workspace.CurrentSolution.WithOptions(workspace.Options
                    .WithChangedOption(CSharpCodeStyleOptions.VarWhenTypeIsApparent, new CodeStyleOption<bool>(false, NotificationOption.Suggestion))));
                analyzerResult = await AnalyzeAsync(workspace, workspace.CurrentSolution.ProjectIds.First(), analyzerType);

                diagnostics = analyzerResult.GetDocumentDiagnostics(analyzerResult.DocumentIds.First(), AnalysisKind.Semantic);
                Assert.Equal(IDEDiagnosticIds.UseExplicitTypeDiagnosticId, diagnostics[0].Id);
                Assert.Equal(DiagnosticSeverity.Info, diagnostics[0].Severity);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.RemoteHost)]
        public async Task TestVisualBasicAnalyzerOptions()
        {
            var code = @"Class Test
    Sub Method()
        Dim b = Nothing
        Dim a = If(b Is Nothing, Nothing, b.ToString())
    End Sub
End Class";

            using (var workspace = CreateWorkspace(LanguageNames.VisualBasic, code))
            {
                // set option
                workspace.TryApplyChanges(workspace.CurrentSolution.WithOptions(workspace.Options
                    .WithChangedOption(CodeStyleOptions2.PreferNullPropagation, LanguageNames.VisualBasic, new CodeStyleOption2<bool>(false, NotificationOption2.Silent))));

                var analyzerType = typeof(VisualBasicUseNullPropagationDiagnosticAnalyzer);
                var analyzerResult = await AnalyzeAsync(workspace, workspace.CurrentSolution.ProjectIds.First(), analyzerType);

                Assert.True(analyzerResult.IsEmpty);

                // set option
                workspace.TryApplyChanges(workspace.CurrentSolution.WithOptions(workspace.Options
                    .WithChangedOption(CodeStyleOptions2.PreferNullPropagation, LanguageNames.VisualBasic, new CodeStyleOption2<bool>(true, NotificationOption2.Error))));
                analyzerResult = await AnalyzeAsync(workspace, workspace.CurrentSolution.ProjectIds.First(), analyzerType);

                var diagnostics = analyzerResult.GetDocumentDiagnostics(analyzerResult.DocumentIds.First(), AnalysisKind.Semantic);
                Assert.Equal(IDEDiagnosticIds.UseNullPropagationDiagnosticId, diagnostics[0].Id);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.RemoteHost)]
        public async Task TestCancellation()
        {
            var code = @"class Test { void Method() { } }";

            using (var workspace = CreateWorkspace(LanguageNames.CSharp, code))
            {
                var analyzerType = typeof(MyAnalyzer);

                for (var i = 0; i < 5; i++)
                {
                    var source = new CancellationTokenSource();

                    try
                    {
                        var task = Task.Run(() => AnalyzeAsync(workspace, workspace.CurrentSolution.ProjectIds.First(), analyzerType, source.Token));

                        // wait random milli-second
                        var random = new Random(Environment.TickCount);
                        var next = random.Next(1000);
                        await Task.Delay(next);

                        source.Cancel();

                        // let it throw
                        var result = await task;
                    }
                    catch (Exception ex)
                    {
                        // only cancellation is expected
                        Assert.True(ex is OperationCanceledException, $"cancellationToken : {source.Token.IsCancellationRequested}/r/n{ex.ToString()}");
                    }
                }
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.RemoteHost)]
        public async Task TestHostAnalyzers_OutOfProc()
        {
            var code = @"class Test
{
    void Method()
    {
        var t = new Test();
    }
}";
            using (var workspace = CreateWorkspace(LanguageNames.CSharp, code))
            {
                var analyzerType = typeof(CSharpUseExplicitTypeDiagnosticAnalyzer);
                var analyzerReference = new AnalyzerFileReference(analyzerType.Assembly.Location, new TestAnalyzerAssemblyLoader());

                var options = workspace.Options
                    .WithChangedOption(CSharpCodeStyleOptions.VarWhenTypeIsApparent, new CodeStyleOption<bool>(false, NotificationOption.Suggestion));

                workspace.TryApplyChanges(workspace.CurrentSolution.WithOptions(options).WithAnalyzerReferences(new[] { analyzerReference }));

                // run analysis
                var project = workspace.CurrentSolution.Projects.First();

                var runner = CreateAnalyzerRunner();

                var compilationWithAnalyzers = (await project.GetCompilationAsync()).WithAnalyzers(
                    analyzerReference.GetAnalyzers(project.Language).Where(a => a.GetType() == analyzerType).ToImmutableArray(),
                    new WorkspaceAnalyzerOptions(project.AnalyzerOptions, project.Solution));

                // no result for open file only analyzer unless forced
                var result = await runner.AnalyzeProjectAsync(project, compilationWithAnalyzers, forceExecuteAllAnalyzers: false, logPerformanceInfo: false, getTelemetryInfo: false, cancellationToken: CancellationToken.None);
                Assert.Empty(result.AnalysisResult);

                result = await runner.AnalyzeProjectAsync(project, compilationWithAnalyzers, forceExecuteAllAnalyzers: true, logPerformanceInfo: false, getTelemetryInfo: false, cancellationToken: CancellationToken.None);
                var analyzerResult = result.AnalysisResult[compilationWithAnalyzers.Analyzers[0]];

                // check result
                var diagnostics = analyzerResult.GetDocumentDiagnostics(analyzerResult.DocumentIds.First(), AnalysisKind.Semantic);
                Assert.Equal(IDEDiagnosticIds.UseExplicitTypeDiagnosticId, diagnostics[0].Id);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.RemoteHost)]
        public async Task TestDuplicatedAnalyzers()
        {
            var code = @"class Test
{
    void Method()
    {
        var t = new Test();
    }
}";

            using (var workspace = CreateWorkspace(LanguageNames.CSharp, code))
            {
                var analyzerType = typeof(DuplicateAnalyzer);
                var analyzerReference = new AnalyzerFileReference(analyzerType.Assembly.Location, new TestAnalyzerAssemblyLoader());

                // add host analyzer as global assets
                var remotableDataService = workspace.Services.GetService<ISolutionAssetStorageProvider>();
                var serializer = workspace.Services.GetRequiredService<ISerializerService>();

                // run analysis
                var project = workspace.CurrentSolution.Projects.First().AddAnalyzerReference(analyzerReference);

                var runner = CreateAnalyzerRunner();
                var analyzers = analyzerReference.GetAnalyzers(project.Language).Where(a => a.GetType() == analyzerType).ToImmutableArray();

                var compilationWithAnalyzers = (await project.GetCompilationAsync())
                    .WithAnalyzers(analyzers, new WorkspaceAnalyzerOptions(project.AnalyzerOptions, project.Solution));

                var result = await runner.AnalyzeProjectAsync(project, compilationWithAnalyzers, forceExecuteAllAnalyzers: false,
                    logPerformanceInfo: false, getTelemetryInfo: false, cancellationToken: CancellationToken.None);

                var analyzerResult = result.AnalysisResult[compilationWithAnalyzers.Analyzers[0]];

                // check result
                var diagnostics = analyzerResult.GetDocumentDiagnostics(analyzerResult.DocumentIds.First(), AnalysisKind.Syntax);
                Assert.Equal("test", diagnostics[0].Id);
            }
        }

        private static InProcOrRemoteHostAnalyzerRunner CreateAnalyzerRunner()
            => new(new DiagnosticAnalyzerInfoCache());

        private static async Task<DiagnosticAnalysisResult> AnalyzeAsync(TestWorkspace workspace, ProjectId projectId, Type analyzerType, CancellationToken cancellationToken = default)
        {
            var executor = CreateAnalyzerRunner();

            var analyzerReference = new AnalyzerFileReference(analyzerType.Assembly.Location, new TestAnalyzerAssemblyLoader());
            var project = workspace.CurrentSolution.GetProject(projectId).AddAnalyzerReference(analyzerReference);

            var analyzerDriver = (await project.GetCompilationAsync()).WithAnalyzers(
                    analyzerReference.GetAnalyzers(project.Language).Where(a => a.GetType() == analyzerType).ToImmutableArray(),
                    new WorkspaceAnalyzerOptions(project.AnalyzerOptions, project.Solution));

            var result = await executor.AnalyzeProjectAsync(project, analyzerDriver, forceExecuteAllAnalyzers: true, logPerformanceInfo: false,
                getTelemetryInfo: false, cancellationToken);

            return result.AnalysisResult[analyzerDriver.Analyzers[0]];
        }

        private TestWorkspace CreateWorkspace(string language, string code, ParseOptions options = null)
        {
            var composition = EditorTestCompositions.EditorFeatures.WithTestHostParts(TestHost.OutOfProcess);

            var workspace = (language == LanguageNames.CSharp) ?
                TestWorkspace.CreateCSharp(code, parseOptions: options, composition: composition) :
                TestWorkspace.CreateVisualBasic(code, parseOptions: options, composition: composition);

            workspace.TryApplyChanges(workspace.CurrentSolution.WithOptions(workspace.Options
                .WithChangedOption(SolutionCrawlerOptions.BackgroundAnalysisScopeOption, LanguageNames.CSharp, BackgroundAnalysisScope.FullSolution)
                .WithChangedOption(SolutionCrawlerOptions.BackgroundAnalysisScopeOption, LanguageNames.VisualBasic, BackgroundAnalysisScope.FullSolution)));

            return workspace;
        }

        [DiagnosticAnalyzer(LanguageNames.CSharp)]
        private class MyAnalyzer : DiagnosticAnalyzer
        {
            private readonly ImmutableArray<DiagnosticDescriptor> _supportedDiagnostics =
                ImmutableArray.Create(new DiagnosticDescriptor("test", "test", "test", "test", DiagnosticSeverity.Error, isEnabledByDefault: true));

            public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => _supportedDiagnostics;

            public override void Initialize(AnalysisContext context)
            {
                context.RegisterSyntaxTreeAction(c =>
                {
                    for (var i = 0; i < 10000; i++)
                    {
                        c.ReportDiagnostic(Diagnostic.Create(_supportedDiagnostics[0], c.Tree.GetLocation(TextSpan.FromBounds(0, 1))));
                    }
                });
            }
        }

        [DiagnosticAnalyzer(LanguageNames.CSharp)]
        private class DuplicateAnalyzer : DiagnosticAnalyzer
        {
            private readonly ImmutableArray<DiagnosticDescriptor> _supportedDiagnostics =
                ImmutableArray.Create(new DiagnosticDescriptor("test", "test", "test", "test", DiagnosticSeverity.Error, isEnabledByDefault: true));

            public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => _supportedDiagnostics;

            public override void Initialize(AnalysisContext context)
            {
                context.RegisterSyntaxTreeAction(c =>
                {
                    c.ReportDiagnostic(Diagnostic.Create(_supportedDiagnostics[0], c.Tree.GetLocation(TextSpan.FromBounds(0, 1))));
                });
            }
        }

        private class MyUpdateSource : AbstractHostDiagnosticUpdateSource
        {
            private readonly Workspace _workspace;

            public MyUpdateSource(Workspace workspace)
            {
                _workspace = workspace;
            }

            public override Workspace Workspace => _workspace;
        }

        private sealed class InvokeThrowsCancellationConnection : RemoteServiceConnection
        {
            private readonly CancellationTokenSource _source;

            public InvokeThrowsCancellationConnection(CancellationTokenSource source)
            {
                _source = source;
            }

            public override void Dispose()
            {
            }

            public override Task RunRemoteAsync(string targetName, Solution solution, IReadOnlyList<object> arguments, CancellationToken cancellationToken)
            {
                // cancel and throw cancellation exception
                _source.Cancel();
                _source.Token.ThrowIfCancellationRequested();

                throw ExceptionUtilities.Unreachable;
            }

            public override Task<T> RunRemoteAsync<T>(string targetName, Solution solution, IReadOnlyList<object> arguments, Func<Stream, CancellationToken, Task<T>> dataReader, CancellationToken cancellationToken)
            {
                // cancel and throw cancellation exception
                _source.Cancel();
                _source.Token.ThrowIfCancellationRequested();

                throw ExceptionUtilities.Unreachable;
            }
        }
    }
}
