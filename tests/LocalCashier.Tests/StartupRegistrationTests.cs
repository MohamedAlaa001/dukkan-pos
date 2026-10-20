using Dukkan.UI;

namespace LocalCashier.Tests;

public class StartupRegistrationTests
{
    [Fact]
    public void Enable_then_disable_round_trips_in_the_current_user_Run_key()
    {
        var before = StartupRegistration.IsEnabled;
        try
        {
            StartupRegistration.Set(true);
            Assert.True(StartupRegistration.IsEnabled);
            StartupRegistration.Set(false);
            Assert.False(StartupRegistration.IsEnabled);
        }
        finally { StartupRegistration.Set(before); }
    }
}
