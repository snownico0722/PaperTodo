use std::panic::{catch_unwind, AssertUnwindSafe};
use std::ptr;
use std::slice;

use ratex_font::{katex_ttf_glyph_char, FontId};
use ratex_layout::{layout, to_display_list, LayoutOptions};
use ratex_parser::parse;
use ratex_types::color::Color;
use ratex_types::display_item::DisplayItem;
use ratex_types::math_style::MathStyle;

const STATUS_OK: i32 = 0;
const STATUS_INVALID_ARGUMENT: i32 = 1;
const STATUS_INVALID_UTF8: i32 = 2;
const STATUS_PARSE_FAILED: i32 = 3;
const STATUS_OUTPUT_TOO_LARGE: i32 = 4;
const STATUS_SERIALIZE_FAILED: i32 = 5;
const STATUS_PANIC: i32 = 6;

const WIRE_PROTOCOL_VERSION: u32 = 1;
const MAX_SOURCE_BYTES: usize = 64 * 1024;
const MAX_OUTPUT_BYTES: usize = 32 * 1024 * 1024;
const MAX_LOGICAL_DIMENSION: f32 = 4096.0;

struct RenderedDisplayList {
    bytes: Vec<u8>,
    logical_width: f32,
    logical_height: f32,
    logical_baseline: f32,
}

#[no_mangle]
pub unsafe extern "C" fn papertodo_math_render(
    source: *const u8,
    source_len: usize,
    font_size: f32,
    display: u8,
    red: u8,
    green: u8,
    blue: u8,
    alpha: u8,
    output: *mut *mut u8,
    output_len: *mut usize,
    logical_width: *mut f32,
    logical_height: *mut f32,
    logical_baseline: *mut f32,
) -> i32 {
    if output.is_null()
        || output_len.is_null()
        || logical_width.is_null()
        || logical_height.is_null()
        || logical_baseline.is_null()
    {
        return STATUS_INVALID_ARGUMENT;
    }

    unsafe {
        *output = ptr::null_mut();
        *output_len = 0;
        *logical_width = 0.0;
        *logical_height = 0.0;
        *logical_baseline = 0.0;
    }

    if source.is_null()
        || source_len == 0
        || source_len > MAX_SOURCE_BYTES
        || !font_size.is_finite()
        || !(4.0..=256.0).contains(&font_size)
    {
        return STATUS_INVALID_ARGUMENT;
    }

    let result = catch_unwind(AssertUnwindSafe(|| {
        let bytes = unsafe { slice::from_raw_parts(source, source_len) };
        let formula = std::str::from_utf8(bytes).map_err(|_| STATUS_INVALID_UTF8)?;
        layout_formula(
            formula,
            font_size,
            display != 0,
            Color::new(
                red as f32 / 255.0,
                green as f32 / 255.0,
                blue as f32 / 255.0,
                alpha as f32 / 255.0,
            ),
        )
    }));

    let rendered = match result {
        Ok(Ok(value)) => value,
        Ok(Err(status)) => return status,
        Err(_) => return STATUS_PANIC,
    };

    let boxed = rendered.bytes.into_boxed_slice();
    let length = boxed.len();
    let data = Box::into_raw(boxed) as *mut u8;
    unsafe {
        *output = data;
        *output_len = length;
        *logical_width = rendered.logical_width;
        *logical_height = rendered.logical_height;
        *logical_baseline = rendered.logical_baseline;
    }
    STATUS_OK
}

#[no_mangle]
pub unsafe extern "C" fn papertodo_math_free(data: *mut u8, length: usize) {
    if data.is_null() || length == 0 {
        return;
    }

    let boxed = ptr::slice_from_raw_parts_mut(data, length);
    unsafe {
        drop(Box::from_raw(boxed));
    }
}

fn layout_formula(
    formula: &str,
    font_size: f32,
    display: bool,
    color: Color,
) -> Result<RenderedDisplayList, i32> {
    if formula.is_empty() || formula.contains('\0') || formula.len() > MAX_SOURCE_BYTES {
        return Err(STATUS_INVALID_ARGUMENT);
    }

    let nodes = parse(formula).map_err(|_| STATUS_PARSE_FAILED)?;
    if nodes.is_empty() {
        return Err(STATUS_PARSE_FAILED);
    }

    let mut layout_options = LayoutOptions::default();
    layout_options.style = if display {
        MathStyle::Display
    } else {
        MathStyle::Text
    };
    layout_options.color = color;

    let root = layout(&nodes, &layout_options);
    let mut display_list = to_display_list(&root);

    // DisplayList keeps real Unicode scalars for web text output. KaTeX TTF cmaps store some
    // mathematical alphanumeric glyphs in ASCII slots. Normalize only the wire copy so WPF can
    // resolve the exact TTF glyph without duplicating RaTeX's mapping rules.
    for item in &mut display_list.items {
        if let DisplayItem::GlyphPath {
            font,
            char_code,
            ..
        } = item
        {
            if let Some(font_id) = FontId::parse(font) {
                *char_code = katex_ttf_glyph_char(font_id, *char_code) as u32;
            }
        }
    }

    let padding = if display { 4.0 } else { 1.5 };
    let logical_width = display_list.width as f32 * font_size + padding * 2.0;
    let logical_height =
        (display_list.height + display_list.depth) as f32 * font_size + padding * 2.0;
    let logical_baseline = padding + display_list.height as f32 * font_size;
    if !logical_width.is_finite()
        || !logical_height.is_finite()
        || !logical_baseline.is_finite()
        || logical_width <= 0.0
        || logical_height <= 0.0
        || logical_width > MAX_LOGICAL_DIMENSION
        || logical_height > MAX_LOGICAL_DIMENSION
    {
        return Err(STATUS_OUTPUT_TOO_LARGE);
    }

    let payload = serde_json::json!({
        "version": WIRE_PROTOCOL_VERSION,
        "display_list": display_list,
    });
    let bytes = serde_json::to_vec(&payload).map_err(|_| STATUS_SERIALIZE_FAILED)?;
    if bytes.is_empty() || bytes.len() > MAX_OUTPUT_BYTES {
        return Err(STATUS_OUTPUT_TOO_LARGE);
    }

    Ok(RenderedDisplayList {
        bytes,
        logical_width,
        logical_height,
        logical_baseline: logical_baseline.clamp(0.0, logical_height),
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use ratex_types::display_item::DisplayList;

    fn assert_display_list(formula: &str, display: bool) {
        let rendered = layout_formula(formula, 18.0, display, Color::BLACK)
            .unwrap_or_else(|status| panic!("formula failed with status {status}: {formula}"));
        let payload: serde_json::Value =
            serde_json::from_slice(&rendered.bytes).expect("wire JSON");
        assert_eq!(
            payload.get("version").and_then(serde_json::Value::as_u64),
            Some(WIRE_PROTOCOL_VERSION as u64)
        );
        let list: DisplayList = serde_json::from_value(
            payload
                .get("display_list")
                .cloned()
                .expect("display_list"),
        )
        .expect("display list");
        assert!(!list.items.is_empty());
        assert!(rendered.logical_width > 0.0);
        assert!(rendered.logical_height > 0.0);
        assert!(rendered.logical_baseline > 0.0);
    }

    #[test]
    fn lays_out_inline_fraction() {
        assert_display_list(r"\frac{a+b}{c}", false);
    }

    #[test]
    fn lays_out_common_multiline_environments() {
        assert_display_list(
            r"\begin{aligned}a &= b + c \\ d &= e + f\end{aligned}",
            true,
        );
        assert_display_list(r"\begin{matrix}1 & 2 \\ 3 & 4\end{matrix}", true);
        assert_display_list(
            r"\begin{cases}x+y=1 \\ x-y=3\end{cases}",
            true,
        );
    }

    #[test]
    fn rejects_empty_formula() {
        assert_eq!(
            layout_formula("", 18.0, false, Color::BLACK).err(),
            Some(STATUS_INVALID_ARGUMENT)
        );
    }
}
