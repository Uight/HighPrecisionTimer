﻿using System.Runtime.InteropServices;
using System.Runtime.Versioning;

[SupportedOSPlatform("windows")]
public class HighPrecisionTimer : IDisposable
{
    private IntPtr iocpHandle;
    private IntPtr waitIocpHandle;
    private IntPtr timerHandle;
    private readonly IntPtr highResKey;
    private bool isRunning;
    private Thread completionThread;
    private long intervalTicks;
    private long nextFireTime;

    public event Action? TimerElapsed;

    private const uint NtCreateWaitCompletionPacketAccessRights = (uint)Win32Interop.TimerAccessMask.TIMER_MODIFY_STATE | (uint)Win32Interop.TimerAccessMask.TIMER_QUERY_STATE;
    private const uint CreateWaitableTimerExWAccessRights = (uint)Win32Interop.TimerAccessMask.TIMER_MODIFY_STATE | (uint)Win32Interop.TimerAccessMask.SYNCHRONIZE;

    public HighPrecisionTimer()
    {

        iocpHandle = Win32Interop.CreateIoCompletionPort(new IntPtr(-1), IntPtr.Zero, IntPtr.Zero, 0);
        if (iocpHandle == IntPtr.Zero)
            throw new InvalidOperationException($"CreateIoCompletionPort failed: {Marshal.GetLastWin32Error()}");

        int status = Win32Interop.NtCreateWaitCompletionPacket(out waitIocpHandle, NtCreateWaitCompletionPacketAccessRights, IntPtr.Zero);
        if (status != 0)
            throw new InvalidOperationException($"NtCreateWaitCompletionPacket failed: {status:X8}");

        timerHandle = Win32Interop.CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, Win32Interop.CreateWaitableTimerFlag_HIGH_RESOLUTION, CreateWaitableTimerExWAccessRights);
        if (timerHandle == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWaitableTimerExW failed: {Marshal.GetLastWin32Error()}");

        highResKey = new IntPtr(1);
    }

    public void Start(TimeSpan interval)
    {
        if (isRunning) return;
        intervalTicks = interval.Ticks;
        isRunning = true;

        nextFireTime = DateTime.UtcNow.Ticks + intervalTicks; // Compute first expiration

        SetTimer();

        completionThread = new Thread(CompletionThreadLoop) { IsBackground = true };
        completionThread.Start();
    }

    public void Stop()
    {
        isRunning = false;
        if (timerHandle != IntPtr.Zero)
        {
            Win32Interop.CancelWaitableTimer(timerHandle);
        }
        completionThread?.Join();
    }

    public void Dispose()
    {
        Stop();

        if (waitIocpHandle != IntPtr.Zero)
        {
            Win32Interop.CloseHandle(waitIocpHandle);
        }

        if (iocpHandle != IntPtr.Zero)
        {
            Win32Interop.CloseHandle(iocpHandle);
        }

        if (timerHandle != IntPtr.Zero)
        {
            Win32Interop.CloseHandle(timerHandle);
        }
    }

    private void SetTimer()
    {
        long dueTime = nextFireTime - DateTime.UtcNow.Ticks; // Calculate absolute expiration
        dueTime = dueTime < 0 ? 0 : -dueTime; // Ensure valid time

        if (!Win32Interop.SetWaitableTimer(timerHandle, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, false))
            throw new InvalidOperationException($"SetWaitableTimer failed: {Marshal.GetLastWin32Error()}");

        int status = Win32Interop.NtAssociateWaitCompletionPacket(
            waitIocpHandle, iocpHandle, timerHandle, highResKey, IntPtr.Zero, 0, IntPtr.Zero, out _
        );

        if (status != 0)
            throw new InvalidOperationException($"NtAssociateWaitCompletionPacket failed: {status:X8}");
    }

    private void CompletionThreadLoop()
    {
        Win32Interop.OVERLAPPED_ENTRY[] entries = new Win32Interop.OVERLAPPED_ENTRY[64];

        while (isRunning)
        {
            if (Win32Interop.GetQueuedCompletionStatusEx(iocpHandle, entries, 64, out uint count, uint.MaxValue, false))
            {
                for (int i = 0; i < count; i++)
                {
                    if (entries[i].lpCompletionKey == highResKey)
                    {
                        nextFireTime += intervalTicks;
                        SetTimer();

                        TimerElapsed?.Invoke();
                    }
                }
            }
        }
    }
}
