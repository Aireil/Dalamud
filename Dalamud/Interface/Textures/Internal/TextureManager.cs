using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Configuration.Internal;
using Dalamud.Data;
using Dalamud.Game;
using Dalamud.Interface.Internal;
using Dalamud.Interface.Textures.Internal.SharedImmediateTextures;
using Dalamud.Logging.Internal;
using Dalamud.Plugin.Services;
using Dalamud.Utility;

using Lumina.Data;
using Lumina.Data.Files;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace Dalamud.Interface.Textures.Internal;

/// <summary>Service responsible for loading and disposing ImGui texture wraps.</summary>
[ServiceManager.EarlyLoadedService]
internal sealed partial class TextureManager
    : IServiceType,
      IDisposable,
      ITextureProvider,
      ITextureSubstitutionProvider,
      ITextureReadbackProvider
{
    private static readonly ModuleLog Log = new(nameof(TextureManager));

    [ServiceManager.ServiceDependency]
    private readonly Dalamud dalamud = Service<Dalamud>.Get();

    [ServiceManager.ServiceDependency]
    private readonly DalamudConfiguration dalamudConfiguration = Service<DalamudConfiguration>.Get();

    [ServiceManager.ServiceDependency]
    private readonly DataManager dataManager = Service<DataManager>.Get();

    [ServiceManager.ServiceDependency]
    private readonly Framework framework = Service<Framework>.Get();

    [ServiceManager.ServiceDependency]
    private readonly InterfaceManager interfaceManager = Service<InterfaceManager>.Get();

    private DynamicPriorityQueueLoader? dynamicPriorityTextureLoader;
    private SharedTextureManager? sharedTextureManager;
    private WicManager? wicManager;
    private bool disposing;
    private ComPtr<ID3D11Device> device;

    [ServiceManager.ServiceConstructor]
    private unsafe TextureManager(InterfaceManager.InterfaceManagerWithScene withScene)
    {
        using var failsafe = new DisposeSafety.ScopedFinalizer();
        failsafe.Add(this.device = new((ID3D11Device*)withScene.Manager.Device!.NativePointer));
        failsafe.Add(this.dynamicPriorityTextureLoader = new(Math.Max(1, Environment.ProcessorCount - 1)));
        failsafe.Add(this.sharedTextureManager = new(this));
        failsafe.Add(this.wicManager = new(this));
        failsafe.Add(this.simpleDrawer = new());
        this.framework.Update += this.BlameTrackerUpdate;
        failsafe.Add(() => this.framework.Update -= this.BlameTrackerUpdate);
        this.simpleDrawer.Setup(this.device.Get());

        failsafe.Cancel();
    }

    /// <summary>Finalizes an instance of the <see cref="TextureManager"/> class.</summary>
    ~TextureManager() => this.ReleaseUnmanagedResources();

    /// <summary>Gets the dynamic-priority queue texture loader.</summary>
    public DynamicPriorityQueueLoader DynamicPriorityTextureLoader
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => this.dynamicPriorityTextureLoader ?? throw new ObjectDisposedException(nameof(TextureManager));
    }

    /// <summary>Gets a simpler drawer.</summary>
    public SimpleDrawerImpl SimpleDrawer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => this.simpleDrawer ?? throw new ObjectDisposedException(nameof(TextureManager));
    }

    /// <summary>Gets the shared texture manager.</summary>
    public SharedTextureManager Shared
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => this.sharedTextureManager ?? throw new ObjectDisposedException(nameof(TextureManager));
    }

    /// <summary>Gets the WIC manager.</summary>
    public WicManager Wic
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => this.wicManager ?? throw new ObjectDisposedException(nameof(TextureManager));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (this.disposing)
            return;

        this.disposing = true;

        Interlocked.Exchange(ref this.dynamicPriorityTextureLoader, null)?.Dispose();
        Interlocked.Exchange(ref this.simpleDrawer, null)?.Dispose();
        Interlocked.Exchange(ref this.sharedTextureManager, null)?.Dispose();
        Interlocked.Exchange(ref this.wicManager, null)?.Dispose();
        this.ReleaseUnmanagedResources();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc/>
    public Task<IDalamudTextureWrap> CreateFromImageAsync(
        ReadOnlyMemory<byte> bytes,
        string? debugName = null,
        CancellationToken cancellationToken = default) =>
        this.DynamicPriorityTextureLoader.LoadAsync(
            null,
            ct => Task.Run(
                () =>
                    this.BlameSetName(
                        this.NoThrottleCreateFromImage(bytes.ToArray(), ct),
                        debugName ??
                        $"{nameof(this.CreateFromImageAsync)}({nameof(bytes)}, {nameof(cancellationToken)})"),
                ct),
            cancellationToken);

    /// <inheritdoc/>
    public Task<IDalamudTextureWrap> CreateFromImageAsync(
        Stream stream,
        bool leaveOpen = false,
        string? debugName = null,
        CancellationToken cancellationToken = default) =>
        this.DynamicPriorityTextureLoader.LoadAsync(
            null,
            async ct =>
            {
                await using var ms = stream.CanSeek ? new MemoryStream((int)stream.Length) : new();
                await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
                return this.BlameSetName(
                    this.NoThrottleCreateFromImage(ms.GetBuffer(), ct),
                    debugName ??
                    $"{nameof(this.CreateFromImageAsync)}({nameof(stream)}, {nameof(leaveOpen)}, {nameof(cancellationToken)})");
            },
            cancellationToken,
            leaveOpen ? null : stream);

    /// <inheritdoc/>
    // It probably doesn't make sense to throttle this, as it copies the passed bytes to GPU without any transformation.
    public IDalamudTextureWrap CreateFromRaw(
        RawImageSpecification specs,
        ReadOnlySpan<byte> bytes,
        string? debugName = null) =>
        this.BlameSetName(
            this.NoThrottleCreateFromRaw(specs, bytes),
            debugName ?? $"{nameof(this.CreateFromRaw)}({nameof(specs)}, {nameof(bytes)})");

    /// <inheritdoc/>
    public Task<IDalamudTextureWrap> CreateFromRawAsync(
        RawImageSpecification specs,
        ReadOnlyMemory<byte> bytes,
        string? debugName = null,
        CancellationToken cancellationToken = default) =>
        this.DynamicPriorityTextureLoader.LoadAsync(
            null,
            _ => Task.FromResult(
                this.BlameSetName(
                    this.NoThrottleCreateFromRaw(specs, bytes.Span),
                    debugName ??
                    $"{nameof(this.CreateFromRawAsync)}({nameof(specs)}, {nameof(bytes)}, {nameof(cancellationToken)})")),
            cancellationToken);

    /// <inheritdoc/>
    public Task<IDalamudTextureWrap> CreateFromRawAsync(
        RawImageSpecification specs,
        Stream stream,
        bool leaveOpen = false,
        string? debugName = null,
        CancellationToken cancellationToken = default) =>
        this.DynamicPriorityTextureLoader.LoadAsync(
            null,
            async ct =>
            {
                await using var ms = stream.CanSeek ? new MemoryStream((int)stream.Length) : new();
                await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
                return this.BlameSetName(
                    this.NoThrottleCreateFromRaw(specs, ms.GetBuffer().AsSpan(0, (int)ms.Length)),
                    debugName ??
                    $"{nameof(this.CreateFromRawAsync)}({nameof(specs)}, {nameof(stream)}, {nameof(leaveOpen)}, {nameof(cancellationToken)})");
            },
            cancellationToken,
            leaveOpen ? null : stream);

    /// <inheritdoc/>
    public IDalamudTextureWrap CreateFromTexFile(TexFile file) =>
        this.BlameSetName(
            this.CreateFromTexFileAsync(file).Result,
            $"{nameof(this.CreateFromTexFile)}({nameof(file)})");

    /// <inheritdoc/>
    public Task<IDalamudTextureWrap> CreateFromTexFileAsync(
        TexFile file,
        string? debugName = null,
        CancellationToken cancellationToken = default) =>
        this.DynamicPriorityTextureLoader.LoadAsync(
            null,
            _ => Task.FromResult(
                this.BlameSetName(
                    this.NoThrottleCreateFromTexFile(file),
                    debugName ?? $"{nameof(this.CreateFromTexFile)}({nameof(file)})")),
            cancellationToken);

    /// <inheritdoc/>
    bool ITextureProvider.IsDxgiFormatSupported(int dxgiFormat) =>
        this.IsDxgiFormatSupported((DXGI_FORMAT)dxgiFormat);

    /// <inheritdoc cref="ITextureProvider.IsDxgiFormatSupported"/>
    public unsafe bool IsDxgiFormatSupported(DXGI_FORMAT dxgiFormat)
    {
        D3D11_FORMAT_SUPPORT supported;
        if (this.device.Get()->CheckFormatSupport(dxgiFormat, (uint*)&supported).FAILED)
            return false;

        const D3D11_FORMAT_SUPPORT required = D3D11_FORMAT_SUPPORT.D3D11_FORMAT_SUPPORT_TEXTURE2D;
        return (supported & required) == required;
    }

    /// <inheritdoc cref="ITextureProvider.CreateFromRaw"/>
    internal unsafe IDalamudTextureWrap NoThrottleCreateFromRaw(
        RawImageSpecification specs,
        ReadOnlySpan<byte> bytes)
    {
        var texd = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)specs.Width,
            Height = (uint)specs.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = (DXGI_FORMAT)specs.DxgiFormat,
            SampleDesc = new(1, 0),
            Usage = D3D11_USAGE.D3D11_USAGE_IMMUTABLE,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
            CPUAccessFlags = 0,
            MiscFlags = 0,
        };
        using var texture = default(ComPtr<ID3D11Texture2D>);
        fixed (void* dataPtr = bytes)
        {
            var subrdata = new D3D11_SUBRESOURCE_DATA { pSysMem = dataPtr, SysMemPitch = (uint)specs.Pitch };
            this.device.Get()->CreateTexture2D(&texd, &subrdata, texture.GetAddressOf()).ThrowOnError();
        }

        var viewDesc = new D3D11_SHADER_RESOURCE_VIEW_DESC
        {
            Format = texd.Format,
            ViewDimension = D3D_SRV_DIMENSION.D3D11_SRV_DIMENSION_TEXTURE2D,
            Texture2D = new() { MipLevels = texd.MipLevels },
        };
        using var view = default(ComPtr<ID3D11ShaderResourceView>);
        this.device.Get()->CreateShaderResourceView((ID3D11Resource*)texture.Get(), &viewDesc, view.GetAddressOf())
            .ThrowOnError();

        var wrap = new UnknownTextureWrap((IUnknown*)view.Get(), specs.Width, specs.Height, true);
        this.BlameSetName(wrap, $"{nameof(this.NoThrottleCreateFromRaw)}({nameof(specs)}, {nameof(bytes)})");
        return wrap;
    }

    /// <summary>Creates a texture from the given <see cref="TexFile"/>. Skips the load throttler; intended to be used
    /// from implementation of <see cref="SharedImmediateTexture"/>s.</summary>
    /// <param name="file">The data.</param>
    /// <returns>The loaded texture.</returns>
    internal IDalamudTextureWrap NoThrottleCreateFromTexFile(TexFile file)
    {
        ObjectDisposedException.ThrowIf(this.disposing, this);

        var buffer = file.TextureBuffer;
        var (dxgiFormat, conversion) = TexFile.GetDxgiFormatFromTextureFormat(file.Header.Format, false);
        if (conversion != TexFile.DxgiFormatConversion.NoConversion ||
            !this.IsDxgiFormatSupported((DXGI_FORMAT)dxgiFormat))
        {
            dxgiFormat = (int)DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
            buffer = buffer.Filter(0, 0, TexFile.TextureFormat.B8G8R8A8);
        }

        var wrap = this.NoThrottleCreateFromRaw(new(buffer.Width, buffer.Height, dxgiFormat), buffer.RawData);
        this.BlameSetName(wrap, $"{nameof(this.NoThrottleCreateFromTexFile)}({nameof(file)})");
        return wrap;
    }

    /// <summary>Creates a texture from the given <paramref name="fileBytes"/>, trying to interpret it as a
    /// <see cref="TexFile"/>.</summary>
    /// <param name="fileBytes">The file bytes.</param>
    /// <returns>The loaded texture.</returns>
    internal IDalamudTextureWrap NoThrottleCreateFromTexFile(ReadOnlySpan<byte> fileBytes)
    {
        ObjectDisposedException.ThrowIf(this.disposing, this);

        if (!TexFileExtensions.IsPossiblyTexFile2D(fileBytes))
            throw new InvalidDataException("The file is not a TexFile.");

        var bytesArray = fileBytes.ToArray();
        var tf = new TexFile();
        typeof(TexFile).GetProperty(nameof(tf.Data))!.GetSetMethod(true)!.Invoke(
            tf,
            new object?[] { bytesArray });
        typeof(TexFile).GetProperty(nameof(tf.Reader))!.GetSetMethod(true)!.Invoke(
            tf,
            new object?[] { new LuminaBinaryReader(bytesArray) });
        // Note: FileInfo and FilePath are not used from TexFile; skip it.

        var wrap = this.NoThrottleCreateFromTexFile(tf);
        this.BlameSetName(wrap, $"{nameof(this.NoThrottleCreateFromTexFile)}({nameof(fileBytes)})");
        return wrap;
    }

    private void ReleaseUnmanagedResources() => this.device.Reset();
}
