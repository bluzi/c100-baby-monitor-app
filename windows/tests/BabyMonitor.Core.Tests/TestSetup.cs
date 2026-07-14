using Xunit;

// MonitorHub is process-wide state (the monitor owns itself — BG-5), and several tests drive it.
// Running test classes in parallel would let them read each other's alarms. Correctness beats speed
// in a suite that guards a baby monitor.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
