
# Feature design for SkipLocalsInit

The feature is to add a new well-known attribute to the compiler, System.Runtime.CompilerServices.SkipLocalsInit that causes method bodies nested "inside" the scope of the attribute to elide the ".locals init" CIL directive that causes the CLR to zero-init local variables and space reserved using the "localloc" instruction. "Nested inside the scope of the attribute" is a concept defined as follows:

1. When the attribute is applied to a module, all emitted methods inside that module, including generated methods, will skip local initialization.

2. When the attribute is applied to a type (including interfaces), all methods transitively inside that type, including generated methods, will skip local initialization. This includes nested types.

3. When the attribute is applied to a method, the method and all methods generated by the compiler which contain user-influenced code inside that method (e.g., local functions, lambdas, async methods) will skip local initialization.

For the above, generated methods will skip local initialization only if they have user code inside them, or a significant number of
compiler-generated locals. For example, the MoveNext method of an async method will skip local initialization, but the constructor of
a display class probably will not, as the display class constructor likely has no locals anyway.

Some notable decisions:

1. Although SkipLocalsInit does not require unsafe code blocks, it does require the `unsafe` flag to be passed to the compiler. This
   is to signal that in some cases code could view unassigned memory, including `stackalloc`. 

2. Due to the compiler not controlling localsinit for other modules when emitting to a netmodule, SkipLocalsInit is not respected on
   the assembly level, even if the AttributeUsage of the SkipLocalsInitAttribute is specified that it is allowed.

3. The `Inherited` property of the SkipLocalsInitAttribute is not respected. SkipLocalsInit does not propagate through inheritance.
