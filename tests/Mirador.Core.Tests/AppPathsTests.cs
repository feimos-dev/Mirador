namespace Mirador.Core.Tests;

public class AppPathsTests
{
    [Fact]
    public void Default_is_Mirador_under_local_app_data()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.Equal(Path.Combine(local, "Mirador"), AppPaths.Default.Root);
    }

    [Fact]
    public void Monado_log_names_do_not_depend_on_the_culture()
    {
        var saved = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("th-TH");
            var p = new AppPaths(@"C:\data\Mirador");
            Assert.Equal(@"C:\data\Mirador\logs\monado-20261103-141502.log",
                p.NewMonadoLogFile(new DateTime(2026, 11, 3, 14, 15, 2)));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = saved;
        }
    }
}
