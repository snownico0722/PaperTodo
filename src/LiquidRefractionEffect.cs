using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;

namespace PaperTodo;

/// <summary>Distorts only the captured background visual, never the editor or controls.
/// Shader Model 2 has a WPF software fallback; no shader compiler or SDK is shipped.</summary>
internal sealed class LiquidRefractionEffect : ShaderEffect
{
    internal static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty("Input", typeof(LiquidRefractionEffect), 0);
    internal static readonly DependencyProperty MapProperty = RegisterPixelShaderSamplerProperty(nameof(Map), typeof(LiquidRefractionEffect), 1);
    internal static readonly DependencyProperty CropProperty = DependencyProperty.Register(nameof(Crop), typeof(Point4D), typeof(LiquidRefractionEffect), new UIPropertyMetadata(new Point4D(1, 1, 0, 0), PixelShaderConstantCallback(0)));
    internal static readonly DependencyProperty ShiftProperty = DependencyProperty.Register(nameof(Shift), typeof(Point), typeof(LiquidRefractionEffect), new UIPropertyMetadata(new Point(), PixelShaderConstantCallback(1)));
    internal static readonly DependencyProperty TintProperty = DependencyProperty.Register(nameof(Tint), typeof(Point4D), typeof(LiquidRefractionEffect), new UIPropertyMetadata(new Point4D(.965, .98, 1, .30), PixelShaderConstantCallback(2)));
    internal static readonly DependencyProperty LightProperty = DependencyProperty.Register(nameof(Light), typeof(Point), typeof(LiquidRefractionEffect), new UIPropertyMetadata(new Point(.25, .05), PixelShaderConstantCallback(3)));
    public Brush Map { get => (Brush)GetValue(MapProperty); set => SetValue(MapProperty, value); }
    public Point4D Crop { get => (Point4D)GetValue(CropProperty); set => SetValue(CropProperty, value); }
    public Point Shift { get => (Point)GetValue(ShiftProperty); set => SetValue(ShiftProperty, value); }
    public Point4D Tint { get => (Point4D)GetValue(TintProperty); set => SetValue(TintProperty, value); }
    public Point Light { get => (Point)GetValue(LightProperty); set => SetValue(LightProperty, value); }
    private static readonly Lazy<byte[]> Bytecode = new(Compile);

    internal LiquidRefractionEffect()
    {
        var shader = new PixelShader();
        using (var stream = new MemoryStream(Bytecode.Value, false)) shader.SetStreamSource(stream);
        shader.Freeze(); PixelShader = shader;
        UpdateShaderValue(InputProperty); UpdateShaderValue(MapProperty);
        UpdateShaderValue(CropProperty); UpdateShaderValue(ShiftProperty);
        UpdateShaderValue(TintProperty); UpdateShaderValue(LightProperty);
    }

    // Captured image includes an overscan margin. Crop also compensates for motion between
    // capture and presentation. Small dispersion is confined to displaced background pixels.
    private const string Source = """
        sampler2D scene : register(s0);
        sampler2D lens : register(s1);
        float4 crop : register(c0);
        float2 shift : register(c1);
        float4 tint : register(c2);
        float2 light : register(c3);
        float4 main(float2 uv : TEXCOORD) : COLOR {
            float3 field = tex2D(lens, uv).rgb;
            float2 normal = (field.rg * 255.0 - 128.0) / 127.0;
            float2 delta = normal * shift;
            float2 at = uv * crop.xy + crop.zw;
            float3 color;
            color.r = tex2D(scene, saturate(at + delta * 1.014)).r;
            color.g = tex2D(scene, saturate(at + delta)).g;
            color.b = tex2D(scene, saturate(at + delta * .986)).b;
            float f = field.b;
            float highlight = saturate(dot(-normal, light - uv) * 2.5 + .12);
            color = lerp(color, tint.rgb, tint.a);
            color = color * (1 - f * .12) + f * highlight * .58;
            return float4(saturate(color), 1);
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
