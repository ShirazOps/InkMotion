using Xunit;

// Bound concurrent Win2D and Media Foundation allocations within x86 address space.
[assembly: CollectionBehavior(MaxParallelThreads = 2)]
