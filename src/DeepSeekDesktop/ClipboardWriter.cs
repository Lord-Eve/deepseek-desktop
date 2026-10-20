using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;

namespace DeepSeekDesktop;

public static class ClipboardWriter
{
    public static async Task<bool> CopyAsync(string text, Action<string>? write = null)
    {
        // Await resumes on the WPF dispatcher so all clipboard calls remain STA.
        write ??= value => Clipboard.SetDataObject(value, true);
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try { write(text); return true; }
            catch (ExternalException ex) when (ex.HResult == unchecked((int)0x800401D0))
            {
                if (attempt == 9) return false;
                await Task.Delay(100);
            }
        }
        return false;
    }
}
