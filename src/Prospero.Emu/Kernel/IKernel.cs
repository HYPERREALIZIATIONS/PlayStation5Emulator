using System;

namespace Prospero.Emu.Cpu
{
    /// <summary>
    /// Abstraction for the "kernel" / OS personality the userland talks to via
    /// the SYSCALL instruction (0x0F 0x05). On PS5 this dispatches through
    /// libkernel into a mix of FreeBSD syscalls and Sony-specific syscalls.
    ///
    /// This is a *partial* kernel: it implements enough to let early program
    /// code (CRT, libkernel stubs, logging, memory reservation, thread/process
    /// setup, file opens) progress toward loading, resource setup, AGC init,
    /// or first frames. Unknown syscalls return a benign error (0) and are
    /// logged so researchers can see what a title needs.
    /// </summary>
    public interface IKernel
    {
        void HandleSyscall(CpuCore cpu);
    }
}
