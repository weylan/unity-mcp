using System.Collections.Generic;
using System;
using System.Threading.Tasks;
using UnityEditor.TestTools.TestRunner.Api;

namespace MCPForUnity.Editor.Services
{
    internal readonly struct TestJobIdentity : IEquatable<TestJobIdentity>
    {
        public TestJobIdentity(string jobId, long generation)
        {
            JobId = jobId;
            Generation = generation;
        }

        public string JobId { get; }
        public long Generation { get; }

        public bool Equals(TestJobIdentity other)
            => string.Equals(JobId, other.JobId, StringComparison.Ordinal)
               && Generation == other.Generation;

        public override bool Equals(object obj)
            => obj is TestJobIdentity other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                return ((JobId != null ? StringComparer.Ordinal.GetHashCode(JobId) : 0) * 397)
                       ^ Generation.GetHashCode();
            }
        }

        public static bool operator ==(TestJobIdentity left, TestJobIdentity right) => left.Equals(right);
        public static bool operator !=(TestJobIdentity left, TestJobIdentity right) => !left.Equals(right);

        public override string ToString() => $"{JobId ?? "(none)"}@{Generation}";
    }

    /// <summary>
    /// Internal owner-aware execution contract used by asynchronous MCP test jobs. The public
    /// <see cref="ITestRunnerService"/> surface remains unchanged for existing callers.
    /// </summary>
    internal interface IJobBoundTestRunnerService
    {
        Task<TestRunResult> RunTestsForJobAsync(
            TestJobIdentity owner,
            TestMode mode,
            TestFilterOptions filterOptions = null);

        void BindPhysicalOwner(TestJobIdentity owner);
        void ClearPhysicalOwner(TestJobIdentity owner);
    }

    /// <summary>
    /// Options for filtering which tests to run.
    /// All properties are optional - null or empty arrays are ignored.
    /// </summary>
    public class TestFilterOptions
    {
        /// <summary>
        /// Full names of specific tests to run (e.g., "MyNamespace.MyTests.TestMethod").
        /// </summary>
        public string[] TestNames { get; set; }

        /// <summary>
        /// Same as TestNames, except it allows for Regex.
        /// </summary>
        public string[] GroupNames { get; set; }

        /// <summary>
        /// NUnit category names to filter by (tests marked with [Category] attribute).
        /// </summary>
        public string[] CategoryNames { get; set; }

        /// <summary>
        /// Assembly names to filter tests by.
        /// </summary>
        public string[] AssemblyNames { get; set; }
    }

    /// <summary>
    /// Provides access to Unity Test Runner data and execution.
    /// </summary>
    public interface ITestRunnerService
    {
        /// <summary>
        /// Retrieve the list of tests for the requested mode(s).
        /// When <paramref name="mode"/> is null, tests for both EditMode and PlayMode are returned.
        /// </summary>
        Task<IReadOnlyList<Dictionary<string, string>>> GetTestsAsync(TestMode? mode);

        /// <summary>
        /// Execute tests for the supplied mode with optional filtering.
        /// </summary>
        /// <param name="mode">The test mode (EditMode or PlayMode).</param>
        /// <param name="filterOptions">Optional filter options to run specific tests. Pass null to run all tests.</param>
        Task<TestRunResult> RunTestsAsync(TestMode mode, TestFilterOptions filterOptions = null);
    }
}
