using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;

namespace Pirsa.Playback.Mpv;

/// <summary>
/// Binds the composition swapchain that mpv creates (the read-only
/// `display-swapchain` property) to a WinUI 3 SwapChainPanel.
/// </summary>
internal static unsafe class SwapChainPanelInterop
{
    // WinUI 3 (Microsoft.UI.Xaml) ISwapChainPanelNative.
    private static readonly Guid IID_ISwapChainPanelNative = new("63aad0b8-7c24-40ff-85a8-640d944cc325");

    /// <summary>Call on the UI thread. Pass 0 to detach.</summary>
    public static void SetSwapChain(SwapChainPanel panel, IntPtr swapChain)
    {
        nint unknown = ((WinRT.IWinRTObject)panel).NativeObject.ThisPtr;
        int hr = Marshal.QueryInterface(unknown, in IID_ISwapChainPanelNative, out nint native);
        Marshal.ThrowExceptionForHR(hr);

        try
        {
            // IUnknown[0..2], ISwapChainPanelNative::SetSwapChain = slot 3.
            var vtbl = *(void***)native;
            var setSwapChain = (delegate* unmanaged[Stdcall]<nint, nint, int>)vtbl[3];
            Marshal.ThrowExceptionForHR(setSwapChain(native, swapChain));
        }
        finally
        {
            Marshal.Release(native);
        }
    }

    /// <summary>
    /// SwapChainPanel presents a swapchain in DIPs unless its matrix transform
    /// maps the buffer pixels back to DIPs. This is the DPI inverse once the
    /// buffer matches the panel's physical size.
    /// </summary>
    public static bool SetTransform(IntPtr swapChain, float scaleX, float scaleY)
    {
        if (swapChain == IntPtr.Zero || !(scaleX > 0) || !(scaleY > 0))
        {
            return false;
        }

        // IDXGISwapChain2::SetMatrixTransform is vtable slot 33:
        // IUnknown (3) + IDXGIObject (4) + IDXGISwapChain (10) +
        // IDXGISwapChain1 (11) + IDXGISwapChain2's first five methods.
        var vtbl = *(void***)swapChain;
        var setMatrix = (delegate* unmanaged[Stdcall]<nint, DxgiMatrix3x2F*, int>)vtbl[33];
        var matrix = new DxgiMatrix3x2F { M11 = scaleX, M22 = scaleY };
        return setMatrix(swapChain, &matrix) >= 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DxgiMatrix3x2F
    {
        public float M11;
        public float M12;
        public float M21;
        public float M22;
        public float M31;
        public float M32;
    }
}
