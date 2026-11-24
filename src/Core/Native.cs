using System;
using System.Runtime.InteropServices;
using System.Text;

namespace WitchKingTrainer.Core
{
    /// <summary>Win32 declarations used by the trainer core.</summary>
    internal static class Native
    {
        public const uint PROCESS_VM_OPERATION = 0x0008, PROCESS_VM_READ = 0x0010, PROCESS_VM_WRITE = 0x0020,
                          PROCESS_QUERY_INFORMATION = 0x0400, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000,
                          PROCESS_SUSPEND_RESUME = 0x0800, SYNCHRONIZE = 0x00100000;
        public const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_PRIVATE = 0x20000;
        public const uint PAGE_READWRITE = 0x04, PAGE_EXECUTE_READWRITE = 0x40;
        public const int ERROR_ACCESS_DENIED = 5;

        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out int read);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out int written);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr addr, IntPtr size, uint type, uint protect);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool VirtualProtectEx(IntPtr h, IntPtr addr, IntPtr size, uint protect, out uint old);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern int VirtualQueryEx(IntPtr h, IntPtr addr, out MEMORY_BASIC_INFORMATION mbi, int len);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool FlushInstructionCache(IntPtr h, IntPtr addr, IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
        [DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr h);
        [DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr h);

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORY_BASIC_INFORMATION
        {
            public IntPtr BaseAddress, AllocationBase; public uint AllocationProtect; public IntPtr RegionSize; public uint State, Protect, Type;
        }

        /// <summary>Converts a 32-bit address to IntPtr without overflow checks (the game is large-address-aware).</summary>
        public static IntPtr Ptr(uint address) { return new IntPtr(unchecked((int)address)); }
    }
}
