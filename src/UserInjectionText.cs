namespace PaperTodo;

// Catalog text is a cold inspection concern. Merely running PaperTodo without opening
// the injection interface does not construct these translated dictionaries.
internal static partial class UserInjectionText
{
    private static readonly Lazy<Dictionary<string, string[]>> Entries = new(CreateEntries);

    internal static string Get(string key, string fallback)
    {
        var language = UiLanguages.EffectiveUiCulture.TwoLetterISOLanguageName switch
        {
            "en" => 0,
            "ja" => 1,
            "ko" => 2,
            _ => -1
        };
        return language >= 0 && Entries.Value.TryGetValue(key, out var values) ? values[language] : fallback;
    }

    private static Dictionary<string, string[]> CreateEntries()
    {
        var entries = new Dictionary<string, string[]>(StringComparer.Ordinal);
        AddDesktop(entries);
        AddContent(entries);
        AddPreview(entries);
        entries["suggestion.range"] = [
            "Move this value to the nearest declared recommendation; the override remains explicit.",
            "指定された推奨範囲内の最も近い値へ調整します。上書きは維持されます。",
            "명시된 권장 범위에서 가장 가까운 값으로 조정합니다. 재정의는 유지됩니다."];
        entries["suggestion.queueRelease"] = [
            "Keep the queue release distance at least as large as its snap distance.",
            "キューの解除距離を吸着距離以上にします。",
            "대기열 해제 거리를 스냅 거리 이상으로 맞춥니다."];
        entries["suggestion.queueSnap"] = [
            "Keep the queue snap distance no larger than its release distance.",
            "キューの吸着距離を解除距離以下にします。",
            "대기열 스냅 거리를 해제 거리 이하로 맞춥니다."];
        entries["suggestion.imageCompression"] = [
            "Keep the compression target within the current stored-image dimension limit.",
            "圧縮後の画像寸法を保存画像の現在の上限内に収めます。",
            "압축 목표 크기를 현재 저장 이미지 크기 제한 안으로 맞춥니다."];
        entries["suggestion.capsuleFont"] = [
            "Reduce the font's em height to the capsule body height. This rough adjustment does not guarantee unclipped text for every font.",
            "フォントの em 高をカプセル本体の高さに合わせます。概算の調整であり、すべてのフォントで文字の欠けを防げるとは限りません。",
            "글꼴의 em 높이를 캡슐 본문 높이에 맞춥니다. 대략적인 조정이며 모든 글꼴에서 잘림을 방지하지는 않습니다."];
        entries["unknown.description"] = [
            "This version does not recognize this key. It is preserved in user.json but has no runtime effect here.",
            "このバージョンはこのキーを認識しません。user.json に保持されますが、現在の動作には反映されません。",
            "이 버전에서 인식하지 못하는 키입니다. user.json에 보존되지만 현재 실행에는 영향을 주지 않습니다."];
        entries["capsule.gap.baseDip.title"] = ["Capsule base gap", "カプセル間隔の基準値", "캡슐 간격 기본값"];
        entries["capsule.gap.baseDip.description"] = [
            "Base gap for the standard preset; narrow and wide still use the preset delta.",
            "標準プリセットの間隔です。狭い・広いプリセットは差分を加減して計算します。",
            "표준 프리셋의 간격입니다. 좁게와 넓게는 프리셋 차이를 빼거나 더합니다."];
        entries["capsule.gap.deltaDip.title"] = ["Capsule gap preset delta", "カプセル間隔のプリセット差分", "캡슐 간격 프리셋 차이"];
        entries["capsule.gap.deltaDip.description"] = [
            "Narrow subtracts this value from the base gap; wide adds it.",
            "狭いプリセットは基準値からこの値を引き、広いプリセットは足します。",
            "좁게는 기본 간격에서 이 값을 빼고, 넓게는 더합니다."];
        entries["capsule.gap.finalDip.title"] = ["Final capsule gap", "カプセルの最終間隔", "최종 캡슐 간격"];
        entries["capsule.gap.finalDip.description"] = [
            "Replace the result computed from the current gap preset and base values.",
            "現在の間隔プリセットと基準値から計算した結果を置き換えます。",
            "현재 간격 프리셋과 기본값으로 계산한 결과를 대체합니다."];
        entries["capsule.gap.finalDip.defaultDescription"] = [
            "Computed from the current gap preset", "現在の間隔プリセットから計算", "현재 간격 프리셋으로 계산"];
        entries["capsule.edge.widthDip.title"] = ["Final edge capsule width", "端のカプセルの最終幅", "가장자리 캡슐 최종 너비"];
        entries["capsule.edge.widthDip.description"] = [
            "Replace the measured edge capsule width, including the interior shadow margin.",
            "内側の影の余白を含む、内容測定後の端のカプセル幅を置き換えます。",
            "내부 그림자 여백을 포함한 가장자리 캡슐의 측정 너비를 대체합니다."];
        entries["capsule.edge.widthDip.defaultDescription"] = [
            "Computed from the icon, title or plugin content", "アイコン・タイトル・プラグイン内容から計算", "아이콘, 제목 또는 플러그인 콘텐츠로 계산"];
        return entries;
    }

    static partial void AddDesktop(Dictionary<string, string[]> entries);
    static partial void AddContent(Dictionary<string, string[]> entries);
    static partial void AddPreview(Dictionary<string, string[]> entries);
}
