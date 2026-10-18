using ClaudeUsageTray.Models;
using ClaudeUsageTray.UI;

namespace ClaudeUsageTray.Tests;

public class SettingsFormTests
{
    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    [Theory]
    [InlineData(AuthMode.ClaudeCodeOAuth, false)]
    [InlineData(AuthMode.ClaudeCodeOAuth, true)]
    [InlineData(AuthMode.WebCookie, true)]
    public void Every_tab_fits_without_a_scroll_bar(AuthMode mode, bool cliAvailable) => RunSta(() =>
    {
        using var form = new SettingsForm(new AppConfig { AuthMode = mode }, startupEnabled: false, credentialAgeDays: 3,
            updateCredential: () => false, removeCredential: () => { }, credentialAvailable: true, mode: mode,
            claudeCliAvailable: cliAvailable, previewOverlayOpacity: _ => { }, resetOverlayPosition: () => { })
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-6000, -6000),
        };
        form.Show();
        Application.DoEvents();

        var tabs = form.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<TabControl>().Single();
        foreach (TabPage page in tabs.TabPages)
        {
            tabs.SelectedTab = page;
            Application.DoEvents();
            var content = (ScrollableControl)page.Controls[0];
            Assert.False(content.VerticalScroll.Visible, $"the {page.Text} tab scrolls");
        }
    });
}
