using System.Windows;
using System.Windows.Threading;

namespace MinecraftModder
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += OnUnhandledException;
        }

        // Beklenmeyen bir hata launcher'ı sessizce kapatmasın; kullanıcıya söylenip devam edilir.
        private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show($"Beklenmeyen bir hata oluştu:\n\n{e.Exception.Message}", "Minecraft Modder",
                MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }
    }
}
