// Throwaway file for WX-44 branch-protection verification.
// This test intentionally fails so CI goes red, allowing us to confirm
// that the branch protection rule on master blocks the merge button.
// The PR carrying this file is closed without merging; this file never
// reaches master.

using Xunit;

namespace MetarParser.Tests;

public class WX44VerificationFailingTest
{
    [Fact]
    public void IntentionalFailure_VerifyBranchProtectionBlocksRedCI()
    {
        Assert.True(false,
            "Intentional WX-44 verification failure. " +
            "If you see this on master, the throwaway PR was merged by mistake — revert it.");
    }
}
