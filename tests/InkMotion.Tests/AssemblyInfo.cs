using Xunit;

// Keep independent Win2D/Media Foundation integration tests within x86 address space.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
