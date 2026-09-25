using System;
using System.Windows.Forms;

namespace Loader
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try
            {
                Application.Run(new MainForm(args));
            }
            catch (Exception ex)
            {
                MessageBox.Show("Фатальная ошибка: " + ex.Message,
                    "Loader", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
