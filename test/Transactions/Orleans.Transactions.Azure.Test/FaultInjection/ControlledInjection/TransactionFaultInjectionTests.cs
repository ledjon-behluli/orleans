using Orleans.Transactions.AzureStorage.Tests;
using Orleans.Transactions.TestKit.xUnit;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.Azure.Tests
{
    /// <summary>
    /// Tests for transaction behavior under controlled fault injection scenarios with Azure Storage.
    /// </summary>
    [TestCategory("AzureStorage"), TestCategory("Transactions"), TestCategory("Functional")]
    public class TransactionFaultInjectionTests : ControlledFaultInjectionTransactionTestRunnerxUnit, IClassFixture<ControlledFaultInjectionTestFixture>
    {
        public TransactionFaultInjectionTests(ControlledFaultInjectionTestFixture fixture, ITestOutputHelper output)
            : base(fixture.GrainFactory, output)
        {
            fixture.EnsurePreconditionsMet();
        }
    }
}
