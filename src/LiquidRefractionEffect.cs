using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;

namespace PaperTodo;

/// <summary>One small optical strip, never a full-window intermediate. The source is an
/// explicit sampler: do not first stretch a captured screenshot into the paper rectangle.
/// Coordinates remain in full-surface DIPs, including across slice seams and mixed DPI.</summary>
internal sealed class LiquidRefractionEffect : ShaderEffect
{
    internal static readonly DependencyProperty SceneProperty = RegisterPixelShaderSamplerProperty(nameof(Scene), typeof(LiquidRefractionEffect), 0, SamplingMode.Bilinear);
    internal static readonly DependencyProperty ProfileProperty = RegisterPixelShaderSamplerProperty(nameof(Profile), typeof(LiquidRefractionEffect), 1, SamplingMode.Bilinear);
    internal static readonly DependencyProperty CropProperty = Constant(nameof(Crop), typeof(Point4D), new Point4D(1, 1, 0, 0), 0);
    internal static readonly DependencyProperty ShiftProperty = Constant(nameof(Shift), typeof(Point), new Point(), 1);
    internal static readonly DependencyProperty TintProperty = Constant(nameof(Tint), typeof(Point4D), new Point4D(.965, .98, 1, .18), 2);
    internal static readonly DependencyProperty LightProperty = Constant(nameof(Light), typeof(Point), new Point(.24, .05), 3);
    internal static readonly DependencyProperty ViewportProperty = Constant(nameof(Viewport), typeof(Point4D), new Point4D(1, 1, 0, 0), 4);
    internal static readonly DependencyProperty ExtentProperty = Constant(nameof(Extent), typeof(Point4D), new Point4D(400, 340, 1d / 18, 1), 5);
    internal static readonly DependencyProperty RadiiProperty = Constant(nameof(Radii), typeof(Point4D), new Point4D(8, 8, 8, 8), 6);
    private static DependencyProperty Constant(string name, Type type, object value, int register) =>
        DependencyProperty.Register(name, type, typeof(LiquidRefractionEffect), new UIPropertyMetadata(value, PixelShaderConstantCallback(register)));
    public Brush Scene { get => (Brush)GetValue(SceneProperty); set => SetValue(SceneProperty, value); }
    public Brush Profile { get => (Brush)GetValue(ProfileProperty); set => SetValue(ProfileProperty, value); }
    public Point4D Crop { get => (Point4D)GetValue(CropProperty); set => SetValue(CropProperty, value); }
    public Point Shift { get => (Point)GetValue(ShiftProperty); set => SetValue(ShiftProperty, value); }
    public Point4D Tint { get => (Point4D)GetValue(TintProperty); set => SetValue(TintProperty, value); }
    public Point Light { get => (Point)GetValue(LightProperty); set => SetValue(LightProperty, value); }
    public Point4D Viewport { get => (Point4D)GetValue(ViewportProperty); set => SetValue(ViewportProperty, value); }
    public Point4D Extent { get => (Point4D)GetValue(ExtentProperty); set => SetValue(ExtentProperty, value); }
    public Point4D Radii { get => (Point4D)GetValue(RadiiProperty); set => SetValue(RadiiProperty, value); }
    private static readonly Lazy<byte[]> Bytecode = new(Compile);

    internal LiquidRefractionEffect()
    {
        var shader = new PixelShader();
        using (var stream = new MemoryStream(Bytecode.Value, false)) shader.SetStreamSource(stream);
        shader.Freeze(); PixelShader = shader;
        Profile = LensDisplacement.ProfileBrush;
        foreach (var property in new[] { SceneProperty, ProfileProperty, CropProperty, ShiftProperty, TintProperty,
                     LightProperty, ViewportProperty, ExtentProperty, RadiiProperty }) UpdateShaderValue(property);
    }

    // A shared Snell/squircle shoulder profile supplies displacement, slope and Fresnel.
    // Rounded-rectangle distance/normals remain analytic; the tiny LUT is size invariant.
    // No animated displacement map, central magnification, or broad white frame.
    // Alpha is premultiplied. Samples outside overscan become transparent instead of
    // stretching stale pixels when dragging faster than the capture source can follow.
    private const string Source = """
        sampler2D scene : register(s0);
        sampler2D opticalProfile : register(s1);
        float4 crop : register(c0);
        float2 shift : register(c1);
        float4 tint : register(c2);
        float2 light : register(c3);
        float4 viewport : register(c4);
        float4 extent : register(c5);
        float4 radii : register(c6);
        float4 main(float2 uv : TEXCOORD) : COLOR {
            float2 global = uv * viewport.xy + viewport.zw;
            float2 side = step(.5, global);
            float radius = lerp(lerp(radii.x, radii.y, side.x), lerp(radii.w, radii.z, side.x), side.y);
            float2 p = (global - .5) * extent.xy;
            float2 q = abs(p) - extent.xy * .5 + radius;
            float2 outside = max(q, 0);
            float len = length(outside);
            float distance = radius - len - min(max(q.x, q.y), 0);
            float t = saturate(distance * extent.z);
            float3 optical = tex2D(opticalProfile, float2(t * (511.0/512.0) + .5/512.0, .5)).rgb;
            float rim = 1-t;
            float horizontal = step(q.y, q.x);
            float2 normal = lerp(float2(horizontal, 1-horizontal), outside / max(len, .0001), step(.0001, len)) * (side*2-1);
            float2 delta = -normal * optical.r * shift * extent.w;
            float2 at = uv * crop.xy + crop.zw + delta;
            clip(float4(at, 1-at));
            float coverage = saturate(rim * 6);
            coverage = coverage * coverage * (3 - 2 * coverage);
            // Subpixel dispersion follows the bend, not an unrelated rainbow outline.
            float3 color = tex2D(scene, saturate(at)).rgb;
            color.r = tex2D(scene, saturate(at + delta * .012)).r;
            color.b = tex2D(scene, saturate(at - delta * .012)).b;
            color = lerp(color, tint.rgb, tint.a);
            float lightness = saturate(dot(-normal, light-global) + .35);
            float sheen = optical.b * (.18 + .62 * lightness);
            color = color * (1 - optical.g * .08) + sheen;
            return float4(saturate(color) * coverage, coverage);
        }
        """;
    private static byte[] Compile()
    {
        var text = Encoding.UTF8.GetBytes(Source);
        var hr = D3DCompile(text, (nuint)text.Length, null, IntPtr.Zero, IntPtr.Zero,
            "main", "ps_2_0", 1u << 15, 0, out var code, out var error);
        try
        {
            if (hr < 0)
                throw new InvalidOperationException("Liquid background shader: " + (error == null ? $"0x{hr:X8}" : Marshal.PtrToStringAnsi(error.GetBufferPointer())));
            var bytes = new byte[checked((int)code!.GetBufferSize())];
            Marshal.Copy(code.GetBufferPointer(), bytes, 0, bytes.Length); return bytes;
        }
        finally
        {
            if (code != null) Marshal.ReleaseComObject(code);
            if (error != null) Marshal.ReleaseComObject(error);
        }
    }
    [ComImport, Guid("8BA5FB08-5195-40E2-AC58-0D989C3A0102"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface Blob
    {
        [PreserveSig] IntPtr GetBufferPointer();
        [PreserveSig] nuint GetBufferSize();
    }
    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private static extern int D3DCompile(byte[] data, nuint length, string? name, IntPtr defines,
        IntPtr include, string entry, string target, uint flags1, uint flags2, out Blob? code, out Blob? errors);
}
