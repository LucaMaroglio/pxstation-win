using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace SpiceKit.Windows.Media;

/// <summary>
/// <c>IMFTransform::ProcessOutput</c> chiamato direttamente, con i riferimenti COM gestiti qui.
/// </summary>
/// <remarks>
/// Il wrapper di Vortice per <c>MFT_OUTPUT_DATA_BUFFER</c> crea un secondo oggetto gestito per lo
/// stesso campione senza un riferimento in più: rilasciarli entrambi libera il campione mentre è
/// ancora in uso (E_UNEXPECTED). Qui il campione fornito resta del chiamante e quello creato dal
/// MFT (con il suo riferimento) viene avvolto una volta sola.
/// </remarks>
internal static unsafe class TransformOutput
{
    /// <summary>Posizione di <c>ProcessOutput</c> nella vtable (IUnknown 3 + 22 metodi di IMFTransform, mftransform.h).</summary>
    private const int ProcessOutputSlot = 25;

    [StructLayout(LayoutKind.Sequential)]
    private struct OutputDataBufferNative
    {
        public int StreamId;
        public nint Sample;
        public int Status;
        public nint Events;
    }

    /// <summary>
    /// Un'uscita dal flusso 0. <paramref name="produced"/> è il campione creato dal MFT (da
    /// rilasciare), oppure <see langword="null"/> se ha scritto in <paramref name="provided"/>.
    /// </summary>
    public static Result Process(IMFTransform transform, IMFSample? provided, out IMFSample? produced)
    {
        ArgumentNullException.ThrowIfNull(transform);
        var self = transform.NativePointer;
        var buffer = new OutputDataBufferNative { Sample = provided?.NativePointer ?? 0 };
        int status;
        var call = (delegate* unmanaged[Stdcall]<nint, int, int, OutputDataBufferNative*, int*, int>)(*(nint**)self)[ProcessOutputSlot];
        var result = new Result(call(self, 0, 1, &buffer, &status));
        if (buffer.Events != 0) Marshal.Release(buffer.Events);
        produced = buffer.Sample != 0 && buffer.Sample != (provided?.NativePointer ?? 0) ? new IMFSample(buffer.Sample) : null;
        return result;
    }
}
