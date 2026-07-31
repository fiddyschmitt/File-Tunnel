using System.Runtime.InteropServices;

namespace ft_tests.Utilities;

public partial class StringUtility
{
    [LibraryImport("shell32.dll", SetLastError = true)]
    public static partial IntPtr CommandLineToArgvW(
        [MarshalAs(UnmanagedType.LPWStr)] string lpCmdLine, out int pNumArgs);

    public static string[] CommandLineToArgs(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out int argc);
        if (argv == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception();
        try
        {
            var args = new string[argc];
            for (var i = 0; i < args.Length; i++)
            {
                var p = Marshal.ReadIntPtr(argv, i * IntPtr.Size);
                args[i] = Marshal.PtrToStringUni(p) ?? string.Empty;
            }

            return args;
        }
        finally
        {
            Marshal.FreeHGlobal(argv);
        }
    }
}