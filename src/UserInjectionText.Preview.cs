using System.Collections.Generic;

namespace PaperTodo;

internal static partial class UserInjectionText
{
    static partial void AddPreview(Dictionary<string, string[]> entries)
    {
        void Add(string id, string[] title, string[] description, bool dynamic = false)
        {
            entries[id + ".title"] = title;
            entries[id + ".description"] = description;
            if (dynamic) entries[id + ".defaultDescription"] = description;
        }

        var profiles = new (string Key, string En, string Ja, string Ko)[]
        {
            ("veryHigh", "Very high", "とても高い", "매우 높음"),
            ("high", "High", "高い", "높음"),
            ("medium", "Medium", "中", "보통"),
            ("low", "Low", "低い", "낮음"),
            ("veryLow", "Very low", "とても低い", "매우 낮음"),
        };
        var profileFields = new (string Key, string En, string Ja, string Ko)[]
        {
            ("initial.minimumObservationMs", "Initial entry: minimum observation", "初回進入・最短観測時間", "최초 진입 · 최소 관찰 시간"),
            ("initial.minimumDelayMs", "Initial entry: minimum adaptive dwell", "初回進入・最短動的待機時間", "최초 진입 · 최소 가변 대기 시간"),
            ("initial.maximumDelayMs", "Initial entry: maximum adaptive dwell", "初回進入・最長動的待機時間", "최초 진입 · 최대 가변 대기 시간"),
            ("initial.passThroughVetoHorizonMs", "Initial entry: pass-through veto horizon", "初回進入・通過判定の予測時間", "최초 진입 · 통과 차단 예측 시간"),
            ("initial.dynamicDelayHorizonMs", "Initial entry: adaptive dwell risk horizon", "初回進入・動的待機のリスク予測時間", "최초 진입 · 가변 대기 위험 예측 시간"),
            ("transfer.minimumObservationMs", "Transfer: minimum observation", "カプセル間移動・最短観測時間", "캡슐 간 이동 · 최소 관찰 시간"),
            ("transfer.minimumDelayMs", "Transfer: minimum adaptive dwell", "カプセル間移動・最短動的待機時間", "캡슐 간 이동 · 최소 가변 대기 시간"),
            ("transfer.maximumDelayMs", "Transfer: maximum adaptive dwell", "カプセル間移動・最長動的待機時間", "캡슐 간 이동 · 최대 가변 대기 시간"),
            ("transfer.passThroughVetoHorizonMs", "Transfer: pass-through veto horizon", "カプセル間移動・通過判定の予測時間", "캡슐 간 이동 · 통과 차단 예측 시간"),
            ("transfer.dynamicDelayHorizonMs", "Transfer: adaptive dwell risk horizon", "カプセル間移動・動的待機のリスク予測時間", "캡슐 간 이동 · 가변 대기 위험 예측 시간"),
            ("stableFallbackMs", "Stable dwell bypass time", "安定滞在による直接許可時間", "안정된 머무름으로 즉시 허용하는 시간"),
            ("minimumDirectionalSpeedDipPerMs", "Minimum directional speed", "方向を持つ移動の最低速度", "방향성 이동 최소 속도"),
            ("minimumDirectionConsistency", "Minimum direction consistency", "方向の一貫性しきい値", "방향 일관성 임계값"),
            ("minimumVerticalDominance", "Minimum vertical dominance", "縦方向の移動比率しきい値", "수직 이동 비율 임계값"),
            ("corridor.minimumSpeedDipPerMs", "Gap crossing: minimum speed", "空白通過・最低移動速度", "빈 공간 통과 · 최소 이동 속도"),
            ("corridor.minimumPathConsistency", "Gap crossing: path consistency", "空白通過・軌跡の一貫性", "빈 공간 통과 · 경로 일관성"),
            ("corridor.targetPaddingDip", "Gap crossing: target geometry padding", "空白通過・対象の幾何的余白", "빈 공간 통과 · 대상 기하 여백"),
            ("corridor.noTargetIntentCloseMs", "Gap crossing: close delay without target intent", "空白通過・移動先の意図消失後の終了待ち", "빈 공간 통과 · 대상 이동 의도가 없을 때 닫기 대기"),
        };
        string[] profileDescription = [
            "Apply rebuilds the cached sensitivity profile; subsequent intent evaluations use this value.",
            "適用時に感度プロファイルのキャッシュを更新し、以後の意図判定でこの値を使います。",
            "적용 시 감도 프로필 캐시를 갱신하며 이후 의도 평가에서 이 값을 사용합니다."];
        foreach (var profile in profiles)
        foreach (var field in profileFields)
        {
            Add($"edge.hover.profile.{profile.Key}.{field.Key}",
                [$"Hover {profile.En}: {field.En}", $"ホバー感度 {profile.Ja}：{field.Ja}", $"호버 감도 {profile.Ko}: {field.Ko}"],
                profileDescription);
        }

        Add("edge.hover.sampleCapacity",
            ["Hover pointer sample capacity", "ホバーポインターのサンプル容量", "호버 포인터 샘플 용량"],
            ["Length of the ring buffer allocated when the intent predictor is created.", "意図予測器の作成時に確保するリングバッファの長さです。", "의도 예측기를 생성할 때 할당하는 순환 버퍼의 길이입니다."]);
        Add("edge.hover.historyWindowMs",
            ["Hover motion history window", "ホバー移動履歴の時間幅", "호버 이동 이력 시간 범위"],
            ["Time window used to estimate recent motion and changes in speed.", "最近の移動と速度変化を推定する時間範囲です。", "최근 이동과 속도 변화를 추정하는 시간 범위입니다."]);
        Add("edge.hover.staleHistoryMs",
            ["Hover history expiration", "ホバー履歴の失効時間", "호버 이력 만료 시간"],
            ["Do not continue using the old trajectory after a sample gap this long.", "サンプルの間隔がこの時間を超えると、古い軌跡を使わなくなります。", "샘플 간격이 이 시간을 넘으면 이전 경로를 더 이상 사용하지 않습니다."]);
        Add("edge.hover.duplicateSampleMs",
            ["Hover duplicate sample interval", "ホバー重複サンプルの間隔", "호버 중복 샘플 간격"],
            ["Interval used to merge repeated samples at the same position.", "同じ位置の繰り返しサンプルをまとめる際の時間間隔です。", "같은 위치의 반복 샘플을 합치는 데 사용하는 시간 간격입니다."]);
        Add("edge.hover.brakingRatio",
            ["Hover braking speed ratio", "ホバー減速の速度比", "호버 감속 속도 비율"],
            ["Ratio used to compare recent vertical speed with the preceding speed when detecting braking.", "減速判定で、最近の縦方向の速度を直前の速度と比較する比率です。", "감속 판정 시 최근 수직 속도를 이전 속도와 비교하는 비율입니다."]);
        Add("edge.hover.brakingDeltaDipPerMs",
            ["Hover braking speed difference", "ホバー減速の速度差", "호버 감속 속도 차이"],
            ["Absolute speed difference also required to identify a braking trend.", "減速傾向の判定に追加で必要な絶対速度差です。", "감속 추세를 판정할 때 추가로 필요한 절대 속도 차이입니다."]);
        Add("edge.hover.accelerationRatio",
            ["Hover acceleration speed ratio", "ホバー加速の速度比", "호버 가속 속도 비율"],
            ["Ratio used to compare recent vertical speed with the preceding speed when detecting acceleration.", "加速判定で、最近の縦方向の速度を直前の速度と比較する比率です。", "가속 판정 시 최근 수직 속도를 이전 속도와 비교하는 비율입니다."]);
        Add("edge.hover.accelerationDeltaDipPerMs",
            ["Hover acceleration speed difference", "ホバー加速の速度差", "호버 가속 속도 차이"],
            ["Absolute speed difference also required to identify an acceleration trend.", "加速傾向の判定に追加で必要な絶対速度差です。", "가속 추세를 판정할 때 추가로 필요한 절대 속도 차이입니다."]);
        Add("edge.hover.corridorGeometryWeight",
            ["Gap-crossing geometry weight", "空白通過の幾何的根拠の重み", "빈 공간 통과 기하 근거 가중치"],
            ["Weight used when combining geometry and motion evidence in the empty region between capsules.", "カプセル間の空白領域で、幾何的な根拠と移動の根拠を組み合わせる重みです。", "캡슐 사이 빈 영역에서 기하 근거와 이동 근거를 결합할 때 사용하는 가중치입니다."]);
        Add("edge.hover.corridorMotionWeight",
            ["Gap-crossing motion weight", "空白通過の移動の根拠の重み", "빈 공간 통과 이동 근거 가중치"],
            ["Weight used when combining geometry and motion evidence in the empty region between capsules.", "カプセル間の空白領域で、幾何的な根拠と移動の根拠を組み合わせる重みです。", "캡슐 사이 빈 영역에서 기하 근거와 이동 근거를 결합할 때 사용하는 가중치입니다."]);

        Add("edge.hover.corridorStrongConfidence",
            ["Gap-crossing strong-evidence threshold", "空白通過の強い根拠のしきい値", "빈 공간 통과 강한 근거 임계값"],
            ["Confidence at which a single type of evidence is enough to retain browsing intent across a gap.", "単独の根拠だけで空白を通過する閲覧意図を維持できる信頼度です。", "한 종류의 근거만으로 빈 공간을 지나는 탐색 의도를 유지할 수 있는 신뢰도입니다."]);
        Add("edge.hover.corridorKeepEnterConfidence",
            ["Gap-crossing keep-alive entry threshold", "空白通過の維持状態に入るしきい値", "빈 공간 통과 유지 상태 진입 임계값"],
            ["Combined confidence needed before the keep-alive state has been latched.", "維持状態がまだ確定していないときに必要な合成信頼度です。", "유지 상태가 아직 고정되지 않았을 때 필요한 종합 신뢰도입니다."]);
        Add("edge.hover.corridorKeepExitConfidence",
            ["Gap-crossing keep-alive exit threshold", "空白通過の維持状態を続けるしきい値", "빈 공간 통과 유지 상태 지속 임계값"],
            ["Combined confidence needed to retain an already latched keep-alive state.", "確定済みの維持状態を継続するために必要な合成信頼度です。", "이미 고정된 유지 상태를 지속하는 데 필요한 종합 신뢰도입니다."]);
        Add("edge.hover.corridorMeaningfulMovementDip",
            ["Gap-crossing meaningful movement", "空白通過の有効移動距離", "빈 공간 통과 유효 이동 거리"],
            ["Distance required to update the timestamp of the last meaningful pointer movement.", "最後の有効なポインター移動の時刻を更新するために必要な距離です。", "마지막 유효 포인터 이동 시각을 갱신하는 데 필요한 거리입니다."]);
        Add("edge.hover.corridorStationaryGraceMs",
            ["Gap-crossing stationary geometry grace", "空白通過の静止後の幾何的猶予", "빈 공간 통과 정지 후 기하 유예 시간"],
            ["Time after pointer motion stops during which geometry evidence retains its original strength.", "ポインター停止後も幾何的な根拠の強さを維持する時間です。", "포인터가 멈춘 후에도 기하 근거의 기존 강도를 유지하는 시간입니다."]);
        Add("edge.hover.corridorMotionFullConfidenceMs",
            ["Gap-crossing full-confidence arrival horizon", "空白通過の最大信頼度を保つ到達予測時間", "빈 공간 통과 최대 신뢰도 도달 예측 시간"],
            ["Motion confidence remains full when the estimated target arrival time is within this horizon.", "対象への予想到達時間がこの範囲内なら、移動の信頼度を最大に保ちます。", "대상까지의 예상 도달 시간이 이 범위 안이면 이동 신뢰도를 최대로 유지합니다."]);
        Add("edge.hover.corridorMotionFadeMs",
            ["Gap-crossing confidence fade horizon", "空白通過の信頼度減衰の終点", "빈 공간 통과 신뢰도 감쇠 종료 시점"],
            ["Motion confidence finishes fading when the estimated target arrival time reaches this value.", "対象への予想到達時間がこの値に達すると、移動の信頼度の減衰が完了します。", "대상까지의 예상 도달 시간이 이 값에 이르면 이동 신뢰도 감쇠가 완료됩니다."]);
        Add("edge.hover.acceleratingVetoHorizonScale",
            ["Accelerating pass-through veto multiplier", "加速時の通過判定時間の倍率", "가속 시 통과 차단 시간 배율"],
            ["Coefficient in the existing hover-intent formula, read by the next evaluation.", "既存のホバー意図の計算式に使う係数で、次回の判定から読み取られます。", "기존 호버 의도 계산식의 계수이며 다음 평가부터 읽습니다."]);
        Add("edge.hover.accelerationRiskIncrement",
            ["Acceleration dwell-risk increment", "加速時の待機リスク増分", "가속 시 대기 위험 증가량"],
            ["Coefficient in the existing hover-intent formula, read by the next evaluation.", "既存のホバー意図の計算式に使う係数で、次回の判定から読み取られます。", "기존 호버 의도 계산식의 계수이며 다음 평가부터 읽습니다."]);
        Add("edge.hover.brakingRiskScale",
            ["Braking dwell-risk multiplier", "減速時の待機リスク倍率", "감속 시 대기 위험 배율"],
            ["Coefficient in the existing hover-intent formula, read by the next evaluation.", "既存のホバー意図の計算式に使う係数で、次回の判定から読み取られます。", "기존 호버 의도 계산식의 계수이며 다음 평가부터 읽습니다."]);
        Add("edge.hover.uncertainTrendRiskScale",
            ["Dwell-risk multiplier without a speed trend", "速度傾向がないときの待機リスク倍率", "속도 추세가 없을 때 대기 위험 배율"],
            ["Coefficient in the existing hover-intent formula, read by the next evaluation.", "既存のホバー意図の計算式に使う係数で、次回の判定から読み取られます。", "기존 호버 의도 계산식의 계수이며 다음 평가부터 읽습니다."]);
        Add("edge.hover.stationaryGeometryConfidenceCap",
            ["Stationary geometry confidence ceiling", "静止後の幾何的信頼度の上限", "정지 후 기하 신뢰도 상한"],
            ["Coefficient in the existing hover-intent formula, read by the next evaluation.", "既存のホバー意図の計算式に使う係数で、次回の判定から読み取られます。", "기존 호버 의도 계산식의 계수이며 다음 평가부터 읽습니다."]);

        Add("edge.preview.activation.transferResidenceMs",
            ["Preview transfer minimum dwell", "プレビュー切替の最短滞在時間", "미리보기 전환 최소 머무름 시간"],
            ["Read by the existing preview-intent and timer paths. Apply cancels pending old waits; subsequent scheduling uses the new values.", "既存のプレビュー意図とタイマーの処理で読み取ります。適用時に古い待機を解除し、以後のスケジュールに新しい値を使います。", "기존 미리보기 의도 및 타이머 처리에서 읽습니다. 적용 시 이전 대기를 취소하며 이후 예약에 새 값을 사용합니다."]);
        Add("edge.preview.activation.postDragResidenceMs",
            ["Post-drag preview minimum dwell", "ドラッグ後のプレビュー最短滞在時間", "드래그 후 미리보기 최소 머무름 시간"],
            ["Read by the existing preview-intent and timer paths. Apply cancels pending old waits; subsequent scheduling uses the new values.", "既存のプレビュー意図とタイマーの処理で読み取ります。適用時に古い待機を解除し、以後のスケジュールに新しい値を使います。", "기존 미리보기 의도 및 타이머 처리에서 읽습니다. 적용 시 이전 대기를 취소하며 이후 예약에 새 값을 사용합니다."]);
        Add("edge.preview.activation.postDragStableMs",
            ["Post-drag preview stable dwell", "ドラッグ後のプレビュー安定滞在時間", "드래그 후 미리보기 안정 머무름 시간"],
            ["Read by the existing preview-intent and timer paths. Apply cancels pending old waits; subsequent scheduling uses the new values.", "既存のプレビュー意図とタイマーの処理で読み取ります。適用時に古い待機を解除し、以後のスケジュールに新しい値を使います。", "기존 미리보기 의도 및 타이머 처리에서 읽습니다. 적용 시 이전 대기를 취소하며 이후 예약에 새 값을 사용합니다."]);
        Add("edge.preview.activation.pointerToleranceDip",
            ["Preview pointer stability tolerance", "プレビューポインターの安定判定許容距離", "미리보기 포인터 안정 허용 거리"],
            ["Read by the existing preview-intent and timer paths. Apply cancels pending old waits; subsequent scheduling uses the new values.", "既存のプレビュー意図とタイマーの処理で読み取ります。適用時に古い待機を解除し、以後のスケジュールに新しい値を使います。", "기존 미리보기 의도 및 타이머 처리에서 읽습니다. 적용 시 이전 대기를 취소하며 이후 예약에 새 값을 사용합니다."]);
        Add("edge.preview.activation.corridorTrackingIntervalMs",
            ["Gap-crossing preview check interval", "空白通過中のプレビュー確認間隔", "빈 공간 통과 중 미리보기 확인 간격"],
            ["Read by the existing preview-intent and timer paths. Apply cancels pending old waits; subsequent scheduling uses the new values.", "既存のプレビュー意図とタイマーの処理で読み取ります。適用時に古い待機を解除し、以後のスケジュールに新しい値を使います。", "기존 미리보기 의도 및 타이머 처리에서 읽습니다. 적용 시 이전 대기를 취소하며 이후 예약에 새 값을 사용합니다."]);
        Add("edge.preview.activation.activationTrackingIntervalMs",
            ["Preview activation intent check interval", "プレビュー有効化の意図確認間隔", "미리보기 활성화 의도 확인 간격"],
            ["Read by the existing preview-intent and timer paths. Apply cancels pending old waits; subsequent scheduling uses the new values.", "既存のプレビュー意図とタイマーの処理で読み取ります。適用時に古い待機を解除し、以後のスケジュールに新しい値を使います。", "기존 미리보기 의도 및 타이머 처리에서 읽습니다. 적용 시 이전 대기를 취소하며 이후 예약에 새 값을 사용합니다."]);
        Add("edge.preview.activation.cursorReadRetryCount",
            ["Preview physical pointer read retries", "プレビューの実ポインター読み取り再試行回数", "미리보기 실제 포인터 읽기 재시도 횟수"],
            ["Read by the existing preview-intent and timer paths. Apply cancels pending old waits; subsequent scheduling uses the new values.", "既存のプレビュー意図とタイマーの処理で読み取ります。適用時に古い待機を解除し、以後のスケジュールに新しい値を使います。", "기존 미리보기 의도 및 타이머 처리에서 읽습니다. 적용 시 이전 대기를 취소하며 이후 예약에 새 값을 사용합니다."]);

        Add("edge.preview.activation.layoutSuppressionMarginMs",
            ["Extra preview suppression after queue movement", "キュー移動後の追加プレビュー抑制時間", "대기열 이동 후 추가 미리보기 억제 시간"],
            ["Added to the current queue slot movement animation duration.", "現在のキュースロット移動アニメーション時間に加算します。", "현재 대기열 슬롯 이동 애니메이션 시간에 더합니다."]);
        Add("edge.preview.activation.layoutSuppressionMs",
            ["Final preview suppression after queue movement", "キュー移動後の最終プレビュー抑制時間", "대기열 이동 후 최종 미리보기 억제 시간"],
            ["Current queue slot movement animation duration plus the extra suppression time.", "現在のキュースロット移動アニメーション時間と追加抑制時間の合計です。", "현재 대기열 슬롯 이동 애니메이션 시간과 추가 억제 시간의 합입니다."], dynamic: true);
        Add("edge.preview.measure.glyphWidthDip",
            ["Preview estimated base glyph width", "プレビュー推定の基準文字幅", "미리보기 추정 기본 글자 너비"],
            ["Used for lightweight width and wrapping estimates; the existing controls still render the content.", "幅と折り返しの軽量な概算に使います。内容の描画は既存のコントロールが行います。", "너비와 줄바꿈을 가볍게 추정하는 데 사용하며 실제 렌더링은 기존 컨트롤이 담당합니다."]);
        Add("edge.preview.measure.chromeReserveWidthDip",
            ["Preview default horizontal reserve", "プレビューの既定の横方向余白", "미리보기 기본 가로 여백"],
            ["Used for lightweight width and wrapping estimates; the existing controls still render the content.", "幅と折り返しの軽量な概算に使います。内容の描画は既存のコントロールが行います。", "너비와 줄바꿈을 가볍게 추정하는 데 사용하며 실제 렌더링은 기존 컨트롤이 담당합니다."]);

        Add("edge.preview.measure.widthLineBudget",
            ["Preview width-estimation line budget", "プレビュー幅推定の行数上限", "미리보기 너비 추정 줄 수 한도"],
            ["Limits the input to lightweight text measurement or the estimated character units per line.", "軽量な文字測定の入力、または1行あたりの推定文字単位を制限します。", "가벼운 텍스트 측정의 입력 또는 줄당 추정 글자 단위를 제한합니다."]);
        Add("edge.preview.measure.widthUnitBudget",
            ["Preview width-estimation character units", "プレビュー幅推定の文字幅単位上限", "미리보기 너비 추정 글자 폭 단위 한도"],
            ["Limits the input to lightweight text measurement or the estimated character units per line.", "軽量な文字測定の入力、または1行あたりの推定文字単位を制限します。", "가벼운 텍스트 측정의 입력 또는 줄당 추정 글자 단위를 제한합니다."]);
        Add("edge.preview.measure.minimumLineUnits",
            ["Preview wrapping minimum character units", "プレビュー折り返し推定の最小文字単位", "미리보기 줄바꿈 추정 최소 글자 단위"],
            ["Limits the input to lightweight text measurement or the estimated character units per line.", "軽量な文字測定の入力、または1行あたりの推定文字単位を制限します。", "가벼운 텍스트 측정의 입력 또는 줄당 추정 글자 단위를 제한합니다."]);
        Add("edge.preview.measure.wrappedLineBudget",
            ["Preview wrapping input-line budget", "プレビュー折り返し推定の入力行数上限", "미리보기 줄바꿈 추정 입력 줄 수 한도"],
            ["Limits the input to lightweight text measurement or the estimated character units per line.", "軽量な文字測定の入力、または1行あたりの推定文字単位を制限します。", "가벼운 텍스트 측정의 입력 또는 줄당 추정 글자 단위를 제한합니다."]);

        Add("edge.preview.todo.itemBudget",
            ["Todo preview item budget", "タスクプレビューの項目数上限", "할 일 미리보기 항목 수 한도"],
            ["Shared by Todo preview capture, estimation and rendering. Takes effect after restart.", "タスクプレビューの取得・推定・描画で共有します。再起動後に反映されます。", "할 일 미리보기의 캡처, 추정, 렌더링에서 공유합니다. 다시 시작한 후 적용됩니다."]);
        Add("edge.preview.todo.itemCharacterBudget",
            ["Todo preview characters per item", "タスクプレビューの項目ごとの文字数上限", "할 일 미리보기 항목당 글자 수 한도"],
            ["Shared by Todo preview capture, estimation and rendering. Takes effect after restart.", "タスクプレビューの取得・推定・描画で共有します。再起動後に反映されます。", "할 일 미리보기의 캡처, 추정, 렌더링에서 공유합니다. 다시 시작한 후 적용됩니다."]);
        Add("edge.preview.todo.linkedTargetButtonSizeDip",
            ["Todo preview link button size", "タスクプレビューのリンクボタン寸法", "할 일 미리보기 링크 버튼 크기"],
            ["Shared by Todo preview capture, estimation and rendering. Takes effect after restart.", "タスクプレビューの取得・推定・描画で共有します。再起動後に反映されます。", "할 일 미리보기의 캡처, 추정, 렌더링에서 공유합니다. 다시 시작한 후 적용됩니다."]);

        Add("edge.preview.todo.textReserveDip",
            ["Todo preview fixed text-lane reserve", "タスクプレビューの文字領域の固定余白", "할 일 미리보기 텍스트 영역 고정 여백"],
            ["The same reserve is added to the width estimate and subtracted for wrapping. It does not change the actual Grid layout.", "幅の推定時に加算し、折り返し推定時に差し引く共通の余白です。実際の Grid レイアウトは変えません。", "너비 추정에는 더하고 줄바꿈 추정에는 빼는 공통 여백입니다. 실제 Grid 레이아웃은 바꾸지 않습니다."]);
        Add("edge.preview.todo.minimumWidthDip",
            ["Todo preview minimum width", "タスクプレビューの最小幅", "할 일 미리보기 최소 너비"],
            ["Keeps the current Todo preview formula and clipping. Cached geometry and host reservation use this value after restart.", "現在のタスクプレビューの計算式とクリッピングを維持します。再起動後に寸法キャッシュとホスト予約領域に反映されます。", "현재 할 일 미리보기 계산식과 잘라내기를 유지합니다. 다시 시작한 후 크기 캐시와 호스트 예약 영역에 반영됩니다."]);
        Add("edge.preview.todo.maximumWidthDip",
            ["Todo preview maximum width", "タスクプレビューの最大幅", "할 일 미리보기 최대 너비"],
            ["Keeps the current Todo preview formula and clipping. Cached geometry and host reservation use this value after restart.", "現在のタスクプレビューの計算式とクリッピングを維持します。再起動後に寸法キャッシュとホスト予約領域に反映されます。", "현재 할 일 미리보기 계산식과 잘라내기를 유지합니다. 다시 시작한 후 크기 캐시와 호스트 예약 영역에 반영됩니다."]);
        Add("edge.preview.todo.minimumHeightDip",
            ["Nonempty Todo preview minimum height", "内容のあるタスクプレビューの最小高さ", "내용이 있는 할 일 미리보기 최소 높이"],
            ["Keeps the current Todo preview formula and clipping. Cached geometry and host reservation use this value after restart.", "現在のタスクプレビューの計算式とクリッピングを維持します。再起動後に寸法キャッシュとホスト予約領域に反映されます。", "현재 할 일 미리보기 계산식과 잘라내기를 유지합니다. 다시 시작한 후 크기 캐시와 호스트 예약 영역에 반영됩니다."]);
        Add("edge.preview.todo.maximumHeightDip",
            ["Nonempty Todo preview maximum height", "内容のあるタスクプレビューの最大高さ", "내용이 있는 할 일 미리보기 최대 높이"],
            ["Keeps the current Todo preview formula and clipping. Cached geometry and host reservation use this value after restart.", "現在のタスクプレビューの計算式とクリッピングを維持します。再起動後に寸法キャッシュとホスト予約領域に反映されます。", "현재 할 일 미리보기 계산식과 잘라내기를 유지합니다. 다시 시작한 후 크기 캐시와 호스트 예약 영역에 반영됩니다."]);
        Add("edge.preview.todo.emptyHeightDip",
            ["Empty Todo preview height", "空のタスクプレビューの高さ", "빈 할 일 미리보기 높이"],
            ["Keeps the current Todo preview formula and clipping. Cached geometry and host reservation use this value after restart.", "現在のタスクプレビューの計算式とクリッピングを維持します。再起動後に寸法キャッシュとホスト予約領域に反映されます。", "현재 할 일 미리보기 계산식과 잘라내기를 유지합니다. 다시 시작한 후 크기 캐시와 호스트 예약 영역에 반영됩니다."]);
        Add("edge.preview.todo.emptyMinimumWidthDip",
            ["Empty Todo preview minimum width", "空のタスクプレビューの最小幅", "빈 할 일 미리보기 최소 너비"],
            ["Keeps the current Todo preview formula and clipping. Cached geometry and host reservation use this value after restart.", "現在のタスクプレビューの計算式とクリッピングを維持します。再起動後に寸法キャッシュとホスト予約領域に反映されます。", "현재 할 일 미리보기 계산식과 잘라내기를 유지합니다. 다시 시작한 후 크기 캐시와 호스트 예약 영역에 반영됩니다."]);
        Add("edge.preview.todo.itemEstimatedLineCap",
            ["Todo preview estimated lines per item", "タスクプレビューの項目ごとの推定行数上限", "할 일 미리보기 항목당 추정 줄 수 상한"],
            ["Keeps the current Todo preview formula and clipping. Cached geometry and host reservation use this value after restart.", "現在のタスクプレビューの計算式とクリッピングを維持します。再起動後に寸法キャッシュとホスト予約領域に反映されます。", "현재 할 일 미리보기 계산식과 잘라내기를 유지합니다. 다시 시작한 후 크기 캐시와 호스트 예약 영역에 반영됩니다."]);
        Add("edge.preview.todo.heightBaseDip",
            ["Todo preview fixed estimated height", "タスクプレビュー高さ推定の固定部分", "할 일 미리보기 높이 추정 고정 부분"],
            ["Keeps the current Todo preview formula and clipping. Cached geometry and host reservation use this value after restart.", "現在のタスクプレビューの計算式とクリッピングを維持します。再起動後に寸法キャッシュとホスト予約領域に反映されます。", "현재 할 일 미리보기 계산식과 잘라내기를 유지합니다. 다시 시작한 후 크기 캐시와 호스트 예약 영역에 반영됩니다."]);
        Add("edge.preview.todo.estimatedLineHeightDip",
            ["Todo preview estimated base line height", "タスクプレビュー推定の基準行高", "할 일 미리보기 추정 기본 줄 높이"],
            ["Keeps the current Todo preview formula and clipping. Cached geometry and host reservation use this value after restart.", "現在のタスクプレビューの計算式とクリッピングを維持します。再起動後に寸法キャッシュとホスト予約領域に反映されます。", "현재 할 일 미리보기 계산식과 잘라내기를 유지합니다. 다시 시작한 후 크기 캐시와 호스트 예약 영역에 반영됩니다."]);

        Add("edge.preview.todo.widthDip",
            ["Todo preview final width", "タスクプレビューの最終幅", "할 일 미리보기 최종 너비"],
            ["Estimated from the title, admitted Todo text and marker widths, using the current empty or nonempty width range.", "タイトル、取り込んだタスク本文、マーカー幅から推定し、空・非空に応じた現在の幅範囲を適用します。", "제목, 허용된 할 일 텍스트와 표시 너비로 추정하고 비어 있는지에 따라 현재 너비 범위를 적용합니다."], dynamic: true);
        Add("edge.preview.todo.heightDip",
            ["Todo preview final height", "タスクプレビューの最終高さ", "할 일 미리보기 최종 높이"],
            ["Computed from the admitted Todo wrapping estimate, fixed height and line height, using the current height range.", "取り込んだタスクの推定折り返し行数、固定高さ、行高から計算し、現在の高さ範囲を適用します。", "허용된 할 일의 예상 줄바꿈 수, 고정 높이, 줄 높이로 계산한 뒤 현재 높이 범위를 적용합니다."], dynamic: true);
        Add("edge.preview.todo.hostHeightDip",
            ["Todo preview reserved host height", "タスクプレビューのホスト予約高さ", "할 일 미리보기 호스트 예약 높이"],
            ["The greater of the original 400 DIP reservation and the current maximum possible Todo preview height.", "従来の予約高さ 400 DIP と、現在のタスクプレビューで可能な最大高さの大きい方です。", "기존 예약 높이 400 DIP와 현재 가능한 할 일 미리보기 최대 높이 중 큰 값입니다."], dynamic: true);
        Add("edge.preview.todo.titleFontDip",
            ["Todo preview base title font size", "タスクプレビューのタイトル基準文字サイズ", "할 일 미리보기 제목 기본 글꼴 크기"],
            ["Font size before global scaling. Existing text clipping remains unchanged.", "全体の拡大率を適用する前の文字サイズです。既存の文字クリッピングは変えません。", "전체 배율 적용 전 글꼴 크기입니다. 기존 텍스트 잘라내기는 유지합니다."]);
        Add("edge.preview.todo.summaryFontDip",
            ["Todo preview base count font size", "タスクプレビューの件数表示の基準文字サイズ", "할 일 미리보기 개수 표시 기본 글꼴 크기"],
            ["Font size before global scaling. Existing text clipping remains unchanged.", "全体の拡大率を適用する前の文字サイズです。既存の文字クリッピングは変えません。", "전체 배율 적용 전 글꼴 크기입니다. 기존 텍스트 잘라내기는 유지합니다."]);
        Add("edge.preview.todo.emptyFontDip",
            ["Empty Todo preview base font size", "空のタスクプレビューの基準文字サイズ", "빈 할 일 미리보기 기본 글꼴 크기"],
            ["Font size before global scaling. Existing text clipping remains unchanged.", "全体の拡大率を適用する前の文字サイズです。既存の文字クリッピングは変えません。", "전체 배율 적용 전 글꼴 크기입니다. 기존 텍스트 잘라내기는 유지합니다."]);
        Add("edge.preview.todo.itemFontDip",
            ["Todo preview base item font size", "タスクプレビューの項目の基準文字サイズ", "할 일 미리보기 항목 기본 글꼴 크기"],
            ["Font size before global scaling. Existing text clipping remains unchanged.", "全体の拡大率を適用する前の文字サイズです。既存の文字クリッピングは変えません。", "전체 배율 적용 전 글꼴 크기입니다. 기존 텍스트 잘라내기는 유지합니다."]);
        Add("edge.preview.todo.markerFontDip",
            ["Todo preview base reminder marker size", "タスクプレビューのリマインダーマーカー基準サイズ", "할 일 미리보기 알림 표시 기본 크기"],
            ["Font size before global scaling. Existing text clipping remains unchanged.", "全体の拡大率を適用する前の文字サイズです。既存の文字クリッピングは変えません。", "전체 배율 적용 전 글꼴 크기입니다. 기존 텍스트 잘라내기는 유지합니다."]);
        Add("edge.preview.todo.truncationFontDip",
            ["Todo preview base truncation marker size", "タスクプレビューの省略表示の基準サイズ", "할 일 미리보기 생략 표시 기본 크기"],
            ["Font size before global scaling. Existing text clipping remains unchanged.", "全体の拡大率を適用する前の文字サイズです。既存の文字クリッピングは変えません。", "전체 배율 적용 전 글꼴 크기입니다. 기존 텍스트 잘라내기는 유지합니다."]);

        Add("edge.preview.markdown.minimumWidthDip",
            ["Note preview minimum width", "ノートプレビューの最小幅", "노트 미리보기 최소 너비"],
            ["Keeps the current note preview formula and clipping. Cached geometry, pre-rendering and host reservation use this value after restart.", "現在のノートプレビューの計算式とクリッピングを維持します。再起動後に寸法キャッシュ、事前描画、ホスト予約領域に反映されます。", "현재 노트 미리보기 계산식과 잘라내기를 유지합니다. 다시 시작한 후 크기 캐시, 사전 렌더링, 호스트 예약 영역에 반영됩니다."]);
        Add("edge.preview.markdown.maximumWidthDip",
            ["Note preview maximum width", "ノートプレビューの最大幅", "노트 미리보기 최대 너비"],
            ["Keeps the current note preview formula and clipping. Cached geometry, pre-rendering and host reservation use this value after restart.", "現在のノートプレビューの計算式とクリッピングを維持します。再起動後に寸法キャッシュ、事前描画、ホスト予約領域に反映されます。", "현재 노트 미리보기 계산식과 잘라내기를 유지합니다. 다시 시작한 후 크기 캐시, 사전 렌더링, 호스트 예약 영역에 반영됩니다."]);
        Add("edge.preview.markdown.minimumHeightDip",
            ["Nonempty note preview minimum height", "内容のあるノートプレビューの最小高さ", "내용이 있는 노트 미리보기 최소 높이"],
            ["Keeps the current note preview formula and clipping. Cached geometry, pre-rendering and host reservation use this value after restart.", "現在のノートプレビューの計算式とクリッピングを維持します。再起動後に寸法キャッシュ、事前描画、ホスト予約領域に反映されます。", "현재 노트 미리보기 계산식과 잘라내기를 유지합니다. 다시 시작한 후 크기 캐시, 사전 렌더링, 호스트 예약 영역에 반영됩니다."]);
        Add("edge.preview.markdown.maximumHeightDip",
            ["Nonempty note preview maximum height", "内容のあるノートプレビューの最大高さ", "내용이 있는 노트 미리보기 최대 높이"],
            ["Keeps the current note preview formula and clipping. Cached geometry, pre-rendering and host reservation use this value after restart.", "現在のノートプレビューの計算式とクリッピングを維持します。再起動後に寸法キャッシュ、事前描画、ホスト予約領域に反映されます。", "현재 노트 미리보기 계산식과 잘라내기를 유지합니다. 다시 시작한 후 크기 캐시, 사전 렌더링, 호스트 예약 영역에 반영됩니다."]);
        Add("edge.preview.markdown.emptyHeightDip",
            ["Empty note preview height", "空のノートプレビューの高さ", "빈 노트 미리보기 높이"],
            ["Keeps the current note preview formula and clipping. Cached geometry, pre-rendering and host reservation use this value after restart.", "現在のノートプレビューの計算式とクリッピングを維持します。再起動後に寸法キャッシュ、事前描画、ホスト予約領域に反映されます。", "현재 노트 미리보기 계산식과 잘라내기를 유지합니다. 다시 시작한 후 크기 캐시, 사전 렌더링, 호스트 예약 영역에 반영됩니다."]);
        Add("edge.preview.markdown.emptyMinimumWidthDip",
            ["Empty note preview minimum width", "空のノートプレビューの最小幅", "빈 노트 미리보기 최소 너비"],
            ["Keeps the current note preview formula and clipping. Cached geometry, pre-rendering and host reservation use this value after restart.", "現在のノートプレビューの計算式とクリッピングを維持します。再起動後に寸法キャッシュ、事前描画、ホスト予約領域に反映されます。", "현재 노트 미리보기 계산식과 잘라내기를 유지합니다. 다시 시작한 후 크기 캐시, 사전 렌더링, 호스트 예약 영역에 반영됩니다."]);
        Add("edge.preview.markdown.widthReserveDip",
            ["Note preview width-estimation reserve", "ノートプレビュー幅推定の固定余白", "노트 미리보기 너비 추정 고정 여백"],
            ["Keeps the current note preview formula and clipping. Cached geometry, pre-rendering and host reservation use this value after restart.", "現在のノートプレビューの計算式とクリッピングを維持します。再起動後に寸法キャッシュ、事前描画、ホスト予約領域に反映されます。", "현재 노트 미리보기 계산식과 잘라내기를 유지합니다. 다시 시작한 후 크기 캐시, 사전 렌더링, 호스트 예약 영역에 반영됩니다."]);
        Add("edge.preview.markdown.bodyWidthReserveDip",
            ["Note preview wrapping width reserve", "ノートプレビュー折り返し推定の控除幅", "노트 미리보기 줄바꿈 추정 제외 너비"],
            ["Keeps the current note preview formula and clipping. Cached geometry, pre-rendering and host reservation use this value after restart.", "現在のノートプレビューの計算式とクリッピングを維持します。再起動後に寸法キャッシュ、事前描画、ホスト予約領域に反映されます。", "현재 노트 미리보기 계산식과 잘라내기를 유지합니다. 다시 시작한 후 크기 캐시, 사전 렌더링, 호스트 예약 영역에 반영됩니다."]);
        Add("edge.preview.markdown.heightBaseDip",
            ["Note preview fixed estimated height", "ノートプレビュー高さ推定の固定部分", "노트 미리보기 높이 추정 고정 부분"],
            ["Keeps the current note preview formula and clipping. Cached geometry, pre-rendering and host reservation use this value after restart.", "現在のノートプレビューの計算式とクリッピングを維持します。再起動後に寸法キャッシュ、事前描画、ホスト予約領域に反映されます。", "현재 노트 미리보기 계산식과 잘라내기를 유지합니다. 다시 시작한 후 크기 캐시, 사전 렌더링, 호스트 예약 영역에 반영됩니다."]);
        Add("edge.preview.markdown.estimatedLineHeightDip",
            ["Note preview estimated base line height", "ノートプレビュー推定の基準行高", "노트 미리보기 추정 기본 줄 높이"],
            ["Keeps the current note preview formula and clipping. Cached geometry, pre-rendering and host reservation use this value after restart.", "現在のノートプレビューの計算式とクリッピングを維持します。再起動後に寸法キャッシュ、事前描画、ホスト予約領域に反映されます。", "현재 노트 미리보기 계산식과 잘라내기를 유지합니다. 다시 시작한 후 크기 캐시, 사전 렌더링, 호스트 예약 영역에 반영됩니다."]);

        Add("edge.preview.markdown.widthDip",
            ["Note preview final width", "ノートプレビューの最終幅", "노트 미리보기 최종 너비"],
            ["Estimated from the title, admitted Markdown text and current scale, using the current width range.", "タイトル、取り込んだ Markdown 本文、現在の拡大率から推定し、現在の幅範囲を適用します。", "제목, 허용된 Markdown 텍스트, 현재 배율로 추정한 뒤 현재 너비 범위를 적용합니다."], dynamic: true);
        Add("edge.preview.markdown.heightDip",
            ["Note preview final height", "ノートプレビューの最終高さ", "노트 미리보기 최종 높이"],
            ["Computed from the admitted Markdown line estimate, fixed height, line height and scale, using the current height range.", "取り込んだ Markdown の推定行数、固定高さ、行高、拡大率から計算し、現在の高さ範囲を適用します。", "허용된 Markdown의 예상 줄 수, 고정 높이, 줄 높이, 배율로 계산한 뒤 현재 높이 범위를 적용합니다."], dynamic: true);
        Add("edge.preview.markdown.textScale",
            ["Note preview estimated text scale", "ノートプレビューの推定文字倍率", "노트 미리보기 추정 텍스트 배율"],
            ["The current note base font size times per-note zoom, rounded, then divided by globally scaled 14 DIP.", "現在のノート基準文字サイズにノート単位の拡大率を掛け、丸めた後に全体拡大率適用済みの 14 DIP で割ります。", "현재 노트 기본 글꼴 크기에 개별 노트 배율을 곱하고 반올림한 뒤 전체 배율이 적용된 14 DIP로 나눕니다."], dynamic: true);
        Add("edge.preview.markdown.hostHeightDip",
            ["Note preview reserved host height", "ノートプレビューのホスト予約高さ", "노트 미리보기 호스트 예약 높이"],
            ["The greater of the original 410 DIP reservation and the current maximum possible note preview height.", "従来の予約高さ 410 DIP と、現在のノートプレビューで可能な最大高さの大きい方です。", "기존 예약 높이 410 DIP와 현재 가능한 노트 미리보기 최대 높이 중 큰 값입니다."], dynamic: true);
        Add("edge.preview.markdown.artifactBodyHeightDip",
            ["Note preview pre-rendered body height budget", "ノートプレビューの本文事前描画高さ上限", "노트 미리보기 본문 사전 렌더링 높이 한도"],
            ["The greater of the original 410 DIP preparation height and the current maximum possible note preview height.", "従来の事前描画高さ 410 DIP と、現在のノートプレビューで可能な最大高さの大きい方です。", "기존 준비 높이 410 DIP와 현재 가능한 노트 미리보기 최대 높이 중 큰 값입니다."], dynamic: true);
        Add("edge.preview.markdown.blockCharacterBudget",
            ["Note preview characters scanned per block", "ノートプレビューのブロックごとの走査文字数", "노트 미리보기 블록당 스캔 글자 수"],
            ["Follows the current total preview character budget by default; an explicit value overrides it independently.", "既定では現在のプレビュー全体の文字数上限に従います。明示した値で個別に上書きできます。", "기본적으로 현재 미리보기 전체 글자 수 한도를 따르며 값을 명시하면 독립적으로 재정의합니다."], dynamic: true);
        Add("edge.preview.markdown.blockBudget",
            ["Note preview block budget", "ノートプレビューのブロック数上限", "노트 미리보기 블록 수 한도"],
            ["Blocks admitted when capturing preview content. Fenced code in Full mode keeps the existing block-counting rule.", "プレビュー内容の取得時に受け入れるブロック数です。Full モードのコードフェンスは既存の数え方を維持します。", "미리보기 내용을 캡처할 때 허용하는 블록 수입니다. Full 모드의 코드 펜스는 기존 블록 계산 규칙을 유지합니다."]);
        Add("edge.preview.markdown.characterBudget",
            ["Note preview total character budget", "ノートプレビューの総文字数上限", "노트 미리보기 전체 글자 수 한도"],
            ["Total character budget shared by capture and measurement, including separators between admitted lines.", "取得と測定で共有する総文字数上限です。取り込んだ行の区切りも含みます。", "캡처와 측정이 공유하는 전체 글자 수 한도이며 허용된 줄 사이의 구분 문자도 포함합니다."]);
        Add("edge.preview.markdown.titleFontDip",
            ["Note preview base title font size", "ノートプレビューのタイトル基準文字サイズ", "노트 미리보기 제목 기본 글꼴 크기"],
            ["Note preview title font size before global font scaling.", "全体の文字拡大率を適用する前のノートプレビューのタイトル文字サイズです。", "전체 글꼴 배율 적용 전 노트 미리보기 제목 글꼴 크기입니다."]);
        Add("edge.preview.fallback.minimumWidthDip",
            ["Default status preview minimum width", "既定の状態プレビューの最小幅", "기본 상태 미리보기 최소 너비"],
            ["Estimation input for the status card shown when dedicated preview content is unavailable.", "専用のプレビュー内容がない場合に表示する状態カードの推定値です。", "전용 미리보기 콘텐츠가 없을 때 표시하는 상태 카드의 추정 입력값입니다."]);
        Add("edge.preview.fallback.maximumWidthDip",
            ["Default status preview maximum width", "既定の状態プレビューの最大幅", "기본 상태 미리보기 최대 너비"],
            ["Estimation input for the status card shown when dedicated preview content is unavailable.", "専用のプレビュー内容がない場合に表示する状態カードの推定値です。", "전용 미리보기 콘텐츠가 없을 때 표시하는 상태 카드의 추정 입력값입니다."]);
        Add("edge.preview.fallback.minimumHeightDip",
            ["Default status preview minimum height", "既定の状態プレビューの最小高さ", "기본 상태 미리보기 최소 높이"],
            ["Estimation input for the status card shown when dedicated preview content is unavailable.", "専用のプレビュー内容がない場合に表示する状態カードの推定値です。", "전용 미리보기 콘텐츠가 없을 때 표시하는 상태 카드의 추정 입력값입니다."]);
        Add("edge.preview.fallback.maximumHeightDip",
            ["Default status preview maximum height", "既定の状態プレビューの最大高さ", "기본 상태 미리보기 최대 높이"],
            ["Estimation input for the status card shown when dedicated preview content is unavailable.", "専用のプレビュー内容がない場合に表示する状態カードの推定値です。", "전용 미리보기 콘텐츠가 없을 때 표시하는 상태 카드의 추정 입력값입니다."]);
        Add("edge.preview.fallback.heightBaseDip",
            ["Default status preview fixed estimated height", "既定の状態プレビュー高さ推定の固定部分", "기본 상태 미리보기 높이 추정 고정 부분"],
            ["Estimation input for the status card shown when dedicated preview content is unavailable.", "専用のプレビュー内容がない場合に表示する状態カードの推定値です。", "전용 미리보기 콘텐츠가 없을 때 표시하는 상태 카드의 추정 입력값입니다."]);
        Add("edge.preview.fallback.minimumTextWidthDip",
            ["Default status preview minimum text width", "既定の状態プレビュー折り返し推定の最小文字幅", "기본 상태 미리보기 줄바꿈 추정 최소 텍스트 너비"],
            ["Estimation input for the status card shown when dedicated preview content is unavailable.", "専用のプレビュー内容がない場合に表示する状態カードの推定値です。", "전용 미리보기 콘텐츠가 없을 때 표시하는 상태 카드의 추정 입력값입니다."]);
        Add("edge.preview.fallback.textReserveDip",
            ["Default status preview wrapping reserve", "既定の状態プレビュー折り返し推定の控除幅", "기본 상태 미리보기 줄바꿈 추정 제외 너비"],
            ["Estimation input for the status card shown when dedicated preview content is unavailable.", "専用のプレビュー内容がない場合に表示する状態カードの推定値です。", "전용 미리보기 콘텐츠가 없을 때 표시하는 상태 카드의 추정 입력값입니다."]);
        Add("edge.preview.fallback.estimatedLineHeightDip",
            ["Default status preview estimated line height", "既定の状態プレビュー推定の基準行高", "기본 상태 미리보기 추정 기본 줄 높이"],
            ["Estimation input for the status card shown when dedicated preview content is unavailable.", "専用のプレビュー内容がない場合に表示する状態カードの推定値です。", "전용 미리보기 콘텐츠가 없을 때 표시하는 상태 카드의 추정 입력값입니다."]);

        Add("edge.preview.fallback.widthDip",
            ["Default status preview final width", "既定の状態プレビューの最終幅", "기본 상태 미리보기 최종 너비"],
            ["Estimated from the current title and status text, using the default status card width range.", "現在のタイトルと状態テキストから推定し、既定の状態カードの幅範囲を適用します。", "현재 제목과 상태 텍스트로 추정한 뒤 기본 상태 카드 너비 범위를 적용합니다."], dynamic: true);
        Add("edge.preview.fallback.heightDip",
            ["Default status preview final height", "既定の状態プレビューの最終高さ", "기본 상태 미리보기 최종 높이"],
            ["Estimated from the current status text wrapping, using the default status card height range.", "現在の状態テキストの折り返しから推定し、既定の状態カードの高さ範囲を適用します。", "현재 상태 텍스트 줄바꿈으로 추정한 뒤 기본 상태 카드 높이 범위를 적용합니다."], dynamic: true);
        Add("edge.preview.plugin.defaultMaximumWidthDip",
            ["Plugin Mini compatibility maximum width", "プラグイン Mini の互換性用の最大幅", "플러그인 Mini 호환성 최대 너비"],
            ["Compatibility baseline used to lock the session maximum width when miniMaxSize is not declared.", "miniMaxSize が宣言されていないとき、セッションの有効な最大幅を確定するための互換性基準です。", "miniMaxSize가 선언되지 않았을 때 세션의 유효 최대 너비를 고정하는 호환성 기준입니다."]);
        Add("edge.preview.plugin.defaultMaximumHeightDip",
            ["Plugin Mini compatibility maximum height", "プラグイン Mini の互換性用の最大高さ", "플러그인 Mini 호환성 최대 높이"],
            ["Compatibility baseline used to lock the session maximum height when miniMaxSize is not declared.", "miniMaxSize が宣言されていないとき、セッションの有効な最大高さを確定するための互換性基準です。", "miniMaxSize가 선언되지 않았을 때 세션의 유효 최대 높이를 고정하는 호환성 기준입니다."]);
        Add("edge.preview.plugin.fallbackHeightDip",
            ["Plugin capsule fallback preview height", "プラグインカプセルの代替プレビュー高さ", "플러그인 캡슐 대체 미리보기 높이"],
            ["Height used when a plugin without a dedicated Mini builds a fallback preview from its capsule content.", "専用 Mini のないプラグインがカプセル内容から代替プレビューを作る際の高さです。", "전용 Mini가 없는 플러그인이 캡슐 콘텐츠로 대체 미리보기를 만들 때 사용하는 높이입니다."]);
        Add("edge.preview.plugin.fallbackMinimumWidthDip",
            ["Plugin capsule fallback minimum width", "プラグインカプセルの代替プレビュー最小幅", "플러그인 캡슐 대체 미리보기 최소 너비"],
            ["Width range for a capsule-derived fallback preview when a plugin has no dedicated Mini.", "専用 Mini のないプラグインで、カプセルから作る代替プレビューの幅範囲です。", "전용 Mini가 없는 플러그인의 캡슐 기반 대체 미리보기 너비 범위입니다."]);
        Add("edge.preview.plugin.fallbackMaximumWidthDip",
            ["Plugin capsule fallback maximum width", "プラグインカプセルの代替プレビュー最大幅", "플러그인 캡슐 대체 미리보기 최대 너비"],
            ["Width range for a capsule-derived fallback preview when a plugin has no dedicated Mini.", "専用 Mini のないプラグインで、カプセルから作る代替プレビューの幅範囲です。", "전용 Mini가 없는 플러그인의 캡슐 기반 대체 미리보기 너비 범위입니다."]);

        Add("edge.preview.plugin.fallbackWidthScale",
            ["Plugin capsule fallback width multiplier", "プラグインカプセルの代替プレビュー幅倍率", "플러그인 캡슐 대체 미리보기 너비 배율"],
            ["Multiplier applied to capsule content width plus the close area and outer margin when estimating fallback width.", "カプセル内容の幅に閉じる領域と外側余白を加え、代替プレビュー幅を推定する際に掛ける倍率です。", "캡슐 콘텐츠 너비에 닫기 영역과 바깥 여백을 더한 뒤 대체 너비를 추정할 때 곱하는 배율입니다."]);
        Add("edge.preview.plugin.fallbackContentWidthDip",
            ["Plugin capsule fallback default content width", "プラグインカプセルの代替用の既定内容幅", "플러그인 캡슐 대체용 기본 콘텐츠 너비"],
            ["Base width for fallback estimation when the capsule does not provide a content width.", "カプセルが内容幅を指定していない場合の代替プレビュー推定用の基準幅です。", "캡슐이 콘텐츠 너비를 제공하지 않을 때 대체 미리보기 추정에 쓰는 기본 너비입니다."]);
        Add("edge.preview.plugin.fallbackWidthDip",
            ["Plugin capsule fallback final width", "プラグインカプセルの代替プレビュー最終幅", "플러그인 캡슐 대체 미리보기 최종 너비"],
            ["Computed from the current capsule content width, close area, outer margin and fallback multiplier, using the fallback width range.", "現在のカプセル内容幅、閉じる領域、外側余白、代替用倍率から計算し、代替プレビューの幅範囲を適用します。", "현재 캡슐 콘텐츠 너비, 닫기 영역, 바깥 여백, 대체 배율로 계산한 뒤 대체 너비 범위를 적용합니다."], dynamic: true);
        Add("edge.preview.monitorInsetDip",
            ["Preview monitor work-area width and height deduction", "プレビュー用のモニター作業領域の幅・高さ控除", "미리보기 모니터 작업 영역 너비·높이 차감"],
            ["Subtracted separately from work-area width and height when calculating available Mini size on the current monitor.", "現在のモニターで利用可能な Mini 寸法を計算するとき、作業領域の幅と高さからそれぞれ差し引きます。", "현재 모니터에서 사용 가능한 Mini 크기를 계산할 때 작업 영역 너비와 높이에서 각각 뺍니다."]);
    }
}
