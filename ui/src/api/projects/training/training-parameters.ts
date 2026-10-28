import type { GpuInventoryResponseDto } from "./response-dto/training-runtime-response-dto";
import type { TrainingParameters, TrainingParameterValue } from "./request-dto/training-request-dto";
import type { TrainingRunState } from "./response-dto/training-run-response-dto";

/**
 * Реестр параметров обучения для формы: ключи протокола worker (camelCase), подписи и подсказки, группы, типы и диапазоны,
 * значения по умолчанию, подобранные в worker (python/worker/protocol/request.py). Проверки диапазонов совпадают с Core
 * (TrainingParameterRules): неверное значение отклоняется кодом training.parameter_invalid с details.field = ключ.
 *
 * lr0, warmupEpochs, patience и workers по умолчанию null — их считает worker; форма показывает фактическое значение через
 * tunedDefaults(). При optimizer «auto» поле lr0 помечается «Подберёт оптимизатор» (isLr0ChosenByOptimizer).
 */

export type TrainingParameterGroup =
    | "training" | "optimizer" | "loss" | "data" | "reproducibility" | "augmentation" | "validation" | "checkpoints";

/** Группы «Дополнительно» в порядке показа; system — «Управление приложением и недоступные настройки». */
export const trainingParameterGroups: readonly { id: TrainingParameterGroup | "system"; label: string }[] = [
    { id: "training", label: "Длительность и режим обучения" },
    { id: "optimizer", label: "Оптимизатор и скорость обучения" },
    { id: "loss", label: "Функция потерь" },
    { id: "data", label: "Данные и загрузка" },
    { id: "reproducibility", label: "Воспроизводимость" },
    { id: "augmentation", label: "Аугментации" },
    { id: "validation", label: "Проверка качества" },
    { id: "checkpoints", label: "Сохранение и отчёты" },
    { id: "system", label: "Управление приложением и недоступные настройки" },
];

/**
 * Вид поля: integer / number — число (min, max; exclusiveMin — строго больше min); boolean — флажок; select — выбор из choices;
 * device — «Устройство» (deviceOptions); list — числа или имена через запятую (parseListField).
 */
export type TrainingParameterType = "integer" | "number" | "boolean" | "select" | "device" | "list";

export interface TrainingParameterChoice {
    value: TrainingParameterValue;
    label: string;
}

export interface TrainingParameterDefinition {
    key: string;
    /** Имя параметра Ultralytics — подпись мелким шрифтом. */
    ultralyticsKey: string;
    label: string;
    hint?: string;
    group: TrainingParameterGroup;
    type: TrainingParameterType;
    /** Подобранное значение по умолчанию; null — «Авто»: значение посчитает worker (см. tunedDefaults). */
    defaultValue: TrainingParameterValue;
    /** null допустим: пустое поле — «Авто». */
    nullable?: boolean;
    min?: number;
    max?: number;
    exclusiveMin?: boolean;
    choices?: readonly TrainingParameterChoice[];
    /** Основное поле формы (видно без «Дополнительно»). */
    base?: boolean;
    /** Переключатель перед полем: выключен — значение offValue, включён — поле активно (onValue — значение при включении). */
    toggle?: { label: string; offValue: number; onValue: number };
}

const onOff = [{ value: true, label: "Вкл" }, { value: false, label: "Выкл" }] as const;

export const trainingParameters: readonly TrainingParameterDefinition[] = [
    // Длительность и режим обучения.
    { key: "epochs", ultralyticsKey: "epochs", label: "Эпохи", group: "training", type: "integer", defaultValue: 100, min: 1, max: 100_000, base: true,
        hint: "Сколько раз модель просмотрит обучающую часть датасета." },
    { key: "batch", ultralyticsKey: "batch", label: "Размер пакета (batch)", group: "training", type: "number", defaultValue: 16, base: true,
        hint: "Целое число кадров за шаг; −1 — AutoBatch; доля от 0 до 1 — доля видеопамяти." },
    { key: "device", ultralyticsKey: "device", label: "Устройство", group: "training", type: "device", defaultValue: "0", base: true,
        hint: "Процессор, номер видеокарты или автовыбор свободной видеокарты (−1)." },
    { key: "amp", ultralyticsKey: "amp", label: "Смешанная точность (AMP)", group: "training", type: "select", defaultValue: true, base: true,
        choices: [{ value: true, label: "Вкл" }, { value: false, label: "Выкл" }, { value: "fp16", label: "fp16" }, { value: "bf16", label: "bf16" }, { value: "fp32", label: "fp32" }],
        hint: "На процессоре и на видеокартах, где смешанная точность даёт сбои, worker отключит её сам; фактическое значение покажет паспорт." },
    { key: "time", ultralyticsKey: "time", label: "Лимит времени, часы", group: "training", type: "number", defaultValue: null, nullable: true, min: 0, exclusiveMin: true,
        hint: "Пусто — без лимита времени. Время может завершить обучение раньше числа эпох." },
    { key: "patience", ultralyticsKey: "patience", label: "Остановка без улучшения, эпох", group: "training", type: "integer", defaultValue: null, nullable: true, min: 0,
        hint: "Пусто — max(10, min(40, эпохи)). 0 отключает раннюю остановку." },
    { key: "pretrained", ultralyticsKey: "pretrained", label: "Начать с предобученных весов", group: "training", type: "boolean", defaultValue: true,
        hint: "Выключено — случайная инициализация модели." },
    { key: "clsRemap", ultralyticsKey: "cls_remap", label: "Переносить веса известных классов", group: "training", type: "boolean", defaultValue: true },
    { key: "freeze", ultralyticsKey: "freeze", label: "Заморозка слоёв", group: "training", type: "list", defaultValue: null, nullable: true,
        hint: "Пусто — без заморозки; число первых слоёв или список индексов/имён через запятую." },
    { key: "channelsLast", ultralyticsKey: "channels_last", label: "Формат памяти channels-last", group: "training", type: "select", defaultValue: null, nullable: true,
        choices: onOff, hint: "Авто — решение установленного runtime." },
    { key: "verbose", ultralyticsKey: "verbose", label: "Подробный журнал", group: "training", type: "boolean", defaultValue: true },
    // Оптимизатор и скорость обучения.
    { key: "optimizer", ultralyticsKey: "optimizer", label: "Оптимизатор", group: "optimizer", type: "select", defaultValue: "AdamW",
        choices: ["auto", "SGD", "MuSGD", "Adam", "Adamax", "AdamW", "NAdam", "RAdam", "RMSProp"].map(value => ({ value, label: value })),
        hint: "auto может выбирать фактическую скорость и моментум; результат покажет паспорт." },
    { key: "lr0", ultralyticsKey: "lr0", label: "Начальная скорость обучения (lr0)", group: "optimizer", type: "number", defaultValue: null, nullable: true, min: 0,
        exclusiveMin: true, base: true, hint: "Пусто — 0,001 · batch / 32. При оптимизаторе auto скорость подберёт оптимизатор." },
    { key: "lrf", ultralyticsKey: "lrf", label: "Итоговая доля скорости обучения", group: "optimizer", type: "number", defaultValue: 0.01, min: 0, max: 1 },
    { key: "momentum", ultralyticsKey: "momentum", label: "Моментум", group: "optimizer", type: "number", defaultValue: 0.9, min: 0, max: 1 },
    { key: "weightDecay", ultralyticsKey: "weight_decay", label: "Регуляризация весов", group: "optimizer", type: "number", defaultValue: 0.0005, min: 0 },
    { key: "warmupEpochs", ultralyticsKey: "warmup_epochs", label: "Разогрев, эпох", group: "optimizer", type: "number", defaultValue: null, nullable: true, min: 0,
        base: true, hint: "Пусто — по числу эпох и batch: не меньше эпохи и не больше четверти обучения." },
    { key: "warmupMomentum", ultralyticsKey: "warmup_momentum", label: "Моментум разогрева", group: "optimizer", type: "number", defaultValue: 0.8, min: 0, max: 1 },
    { key: "warmupBiasLr", ultralyticsKey: "warmup_bias_lr", label: "Скорость разогрева смещений", group: "optimizer", type: "number", defaultValue: 0, min: 0 },
    { key: "cosLr", ultralyticsKey: "cos_lr", label: "Косинусное снижение скорости", group: "optimizer", type: "boolean", defaultValue: false },
    // Функция потерь.
    { key: "box", ultralyticsKey: "box", label: "Вес потерь рамок", group: "loss", type: "number", defaultValue: 7.5, min: 0 },
    { key: "cls", ultralyticsKey: "cls", label: "Вес потерь классов", group: "loss", type: "number", defaultValue: 1, min: 0 },
    { key: "clsPw", ultralyticsKey: "cls_pw", label: "Вес положительных классов", group: "loss", type: "number", defaultValue: 0, min: 0 },
    { key: "dfl", ultralyticsKey: "dfl", label: "Вес DFL", group: "loss", type: "number", defaultValue: 1.5, min: 0 },
    { key: "nbs", ultralyticsKey: "nbs", label: "Номинальный размер батча", group: "loss", type: "integer", defaultValue: 64, min: 1 },
    // Данные и загрузка.
    { key: "cache", ultralyticsKey: "cache", label: "Кэш изображений", group: "data", type: "select", defaultValue: false,
        choices: [{ value: false, label: "Выкл" }, { value: true, label: "Вкл" }, { value: "ram", label: "ram" }, { value: "disk", label: "disk" }] },
    { key: "workers", ultralyticsKey: "workers", label: "Потоки загрузки", group: "data", type: "integer", defaultValue: null, nullable: true, min: 0, max: 256,
        hint: "Пусто — 0 на Windows: загрузка в главном процессе надёжнее." },
    { key: "singleCls", ultralyticsKey: "single_cls", label: "Объединить все классы в один", group: "data", type: "boolean", defaultValue: false,
        hint: "Все выбранные классы будут объединены в один выход модели." },
    { key: "rect", ultralyticsKey: "rect", label: "Прямоугольные батчи", group: "data", type: "boolean", defaultValue: false },
    { key: "fraction", ultralyticsKey: "fraction", label: "Доля обучающих данных", group: "data", type: "list", defaultValue: 1,
        hint: "Число от 0 до 1 либо список долей по выборкам через запятую." },
    { key: "classes", ultralyticsKey: "classes", label: "Индексы классов для обучения", group: "data", type: "list", defaultValue: null, nullable: true,
        hint: "Пусто — все классы; индексы из карты датасета через запятую." },
    // Воспроизводимость.
    { key: "seed", ultralyticsKey: "seed", label: "Seed", group: "reproducibility", type: "integer", defaultValue: 0, min: 0, max: 2_147_483_647, base: true },
    { key: "deterministic", ultralyticsKey: "deterministic", label: "Детерминированный режим", group: "reproducibility", type: "boolean", defaultValue: true },
    // Аугментации.
    { key: "hsvH", ultralyticsKey: "hsv_h", label: "Изменение оттенка", group: "augmentation", type: "number", defaultValue: 0.01, min: 0, max: 1 },
    { key: "hsvS", ultralyticsKey: "hsv_s", label: "Изменение насыщенности", group: "augmentation", type: "number", defaultValue: 0.4, min: 0, max: 1 },
    { key: "hsvV", ultralyticsKey: "hsv_v", label: "Изменение яркости", group: "augmentation", type: "number", defaultValue: 0.5, min: 0, max: 1 },
    { key: "degrees", ultralyticsKey: "degrees", label: "Повороты, градусы", group: "augmentation", type: "number", defaultValue: 0, min: 0, max: 180,
        toggle: { label: "Повороты", offValue: 0, onValue: 10 },
        hint: "Положительное значение задаёт случайный угол от −значения до +значения." },
    { key: "translate", ultralyticsKey: "translate", label: "Сдвиг изображения", group: "augmentation", type: "number", defaultValue: 0.05, min: 0, max: 1,
        hint: "Доля ширины и высоты, 0 выключает сдвиги." },
    { key: "scale", ultralyticsKey: "scale", label: "Масштаб изображения", group: "augmentation", type: "list", defaultValue: 0.1,
        hint: "Число для симметричного диапазона или два множителя min, max через запятую." },
    { key: "shear", ultralyticsKey: "shear", label: "Скос, градусы", group: "augmentation", type: "number", defaultValue: 0, min: 0, max: 180 },
    { key: "perspective", ultralyticsKey: "perspective", label: "Перспектива", group: "augmentation", type: "number", defaultValue: 0, min: 0, max: 1 },
    { key: "flipud", ultralyticsKey: "flipud", label: "Вероятность отражения по вертикали", group: "augmentation", type: "number", defaultValue: 0, min: 0, max: 1 },
    { key: "fliplr", ultralyticsKey: "fliplr", label: "Вероятность отражения по горизонтали", group: "augmentation", type: "number", defaultValue: 0, min: 0, max: 1 },
    { key: "bgr", ultralyticsKey: "bgr", label: "Вероятность смены RGB на BGR", group: "augmentation", type: "number", defaultValue: 0, min: 0, max: 1 },
    { key: "mosaic", ultralyticsKey: "mosaic", label: "Вероятность mosaic", group: "augmentation", type: "number", defaultValue: 0, min: 0, max: 1 },
    { key: "closeMosaic", ultralyticsKey: "close_mosaic", label: "Отключить mosaic за эпох до конца", group: "augmentation", type: "integer", defaultValue: 10, min: 0 },
    { key: "mixup", ultralyticsKey: "mixup", label: "Вероятность mixup", group: "augmentation", type: "number", defaultValue: 0, min: 0, max: 1 },
    { key: "cutmix", ultralyticsKey: "cutmix", label: "Вероятность cutmix", group: "augmentation", type: "number", defaultValue: 0, min: 0, max: 1 },
    { key: "multiScale", ultralyticsKey: "multi_scale", label: "Диапазон изменения размера входа", group: "augmentation", type: "number", defaultValue: 0, min: 0,
        hint: "0 выключает случайное изменение размера входа." },
    // Проверка качества.
    { key: "val", ultralyticsKey: "val", label: "Проверять качество после эпох", group: "validation", type: "boolean", defaultValue: true,
        hint: "Выключено — без проверки после каждой эпохи. Итоговую проверку выполняет установленный runtime." },
    { key: "split", ultralyticsKey: "split", label: "Выборка проверки", group: "validation", type: "select", defaultValue: "val",
        choices: [{ value: "val", label: "val" }, { value: "test", label: "test" }, { value: "train", label: "train" }],
        hint: "Используется для проверки качества; train не даёт независимой оценки." },
    { key: "conf", ultralyticsKey: "conf", label: "Порог уверенности проверки", group: "validation", type: "number", defaultValue: null, nullable: true, min: 0, max: 1,
        hint: "Пусто — порог выбирает runtime." },
    { key: "iou", ultralyticsKey: "iou", label: "Порог IoU", group: "validation", type: "number", defaultValue: 0.7, min: 0, max: 1 },
    { key: "maxDet", ultralyticsKey: "max_det", label: "Максимум объектов в кадре", group: "validation", type: "integer", defaultValue: 300, min: 1 },
    { key: "saveJson", ultralyticsKey: "save_json", label: "Сохранять результаты проверки в JSON", group: "validation", type: "boolean", defaultValue: false },
    // Сохранение и отчёты.
    { key: "save", ultralyticsKey: "save", label: "Сохранять веса во время обучения", group: "checkpoints", type: "boolean", defaultValue: true,
        hint: "Выключено — без промежуточных весов; итоговая модель сохраняется обязательно." },
    { key: "savePeriod", ultralyticsKey: "save_period", label: "Период сохранения, эпох", group: "checkpoints", type: "integer", defaultValue: -1, min: -1,
        hint: "−1 — без дополнительных периодических снимков." },
    { key: "plots", ultralyticsKey: "plots", label: "Сохранять графики и примеры", group: "checkpoints", type: "boolean", defaultValue: true },
];

export const trainingParameterKeys: readonly string[] = trainingParameters.map(parameter => parameter.key);

const byKey = new Map(trainingParameters.map(parameter => [parameter.key, parameter]));

export function trainingParameter(key: string): TrainingParameterDefinition | undefined {
    return byKey.get(key);
}

/** Ключи, недоступные в форме: system — управляет приложение, unsupported — нет в закреплённой сборке, not_applicable — не для detect train. */
export interface UnavailableTrainingParameter {
    key: string;
    status: "system" | "unsupported" | "not_applicable";
    reason: string;
}

const systemReason = "Управляется приложением: выбранная модель, неизменяемый датасет и рабочая папка запуска.";
const otherMode = "Параметр другого режима: predict, val, export или track; не изменяет detect train.";
const notDetect = "Не применяется к задаче detect.";

export const unavailableTrainingParameters: readonly UnavailableTrainingParameter[] = [
    ...["task", "mode", "model", "data", "imgsz", "project", "name", "exist_ok", "cfg"].map(key => ({ key, status: "system" as const, reason: systemReason })),
    { key: "profile", status: "unsupported", reason: "Профилирование требует TensorRT, ONNXRuntime и внешних интеграций, отсутствующих в текущей сборке." },
    { key: "compile", status: "unsupported", reason: "Компиляция требует Triton, отсутствующий в закреплённой Windows-сборке." },
    { key: "resume", status: "unsupported", reason: "Продолжение требует сохранённого checkpoint с optimizer state; готовые версии содержат веса для инференса." },
    { key: "quantize", status: "unsupported", reason: "QAT требует отдельного совместимого пути экспорта и проверки артефактов; текущий паспорт поддерживает обычные веса." },
    { key: "distill_model", status: "unsupported", reason: "Учитель должен быть локальной проверенной моделью с совместимой архитектурой; выбор teacher-модели пока не реализован." },
    { key: "dis", status: "unsupported", reason: "Вес distillation доступен только вместе с проверенной teacher-моделью." },
    ...["overlap_mask", "mask_ratio", "dropout", "pose", "kobj", "rle", "angle", "dlog", "dgrad", "dlam", "copy_paste", "copy_paste_mode", "auto_augment", "erasing"]
        .map(key => ({ key, status: "not_applicable" as const, reason: notDetect })),
    ...["dnn", "nms", "source", "vid_stride", "stream_buffer", "visualize", "augment", "agnostic_nms", "retina_masks", "embed", "show", "save_frames", "save_txt",
        "save_conf", "save_crop", "show_labels", "show_conf", "show_boxes", "line_width", "format", "keras", "optimize", "dynamic", "simplify", "opset", "workspace",
        "tracker"].map(key => ({ key, status: "not_applicable" as const, reason: otherMode })),
];

/** Все поддерживаемые параметры со значениями по умолчанию, поверх них — переданные (пресет, форма). */
export function trainingParametersWithDefaults(values: TrainingParameters = {}): TrainingParameters {
    const result: TrainingParameters = {};
    for (const parameter of trainingParameters) result[parameter.key] = Object.hasOwn(values, parameter.key) ? values[parameter.key] : parameter.defaultValue;
    return result;
}

/**
 * Значения, которые worker посчитает для «Авто» полей: lr0 = 0,001 · batch / 32 (для AutoBatch и долей — по batch 16), разогрев
 * max(1, min(5, эпохи / 10)) · batch / 32 не меньше эпохи и не больше четверти обучения, patience = max(10, min(40, эпохи)), workers 0.
 */
export function tunedDefaults(epochs: number, batch: number): { lr0: number; warmupEpochs: number; patience: number; workers: number } {
    const reference = batch >= 1 ? Math.floor(batch) : 16;
    const safeEpochs = Math.max(1, Math.floor(epochs));
    const base = Math.max(1, Math.min(5, safeEpochs / 10));
    const warmup = Math.min(base * Math.max(1, reference) / 32, Math.max(1, safeEpochs * 0.25));
    return {
        lr0: Math.round(0.001 * Math.max(1, reference) / 32 * 1e6) / 1e6,
        warmupEpochs: Math.round(warmup * 100) / 100,
        patience: Math.max(10, Math.min(40, safeEpochs)),
        workers: 0,
    };
}

/** При optimizer «auto» lr0 выбирает оптимизатор: поле помечается «Подберёт оптимизатор». */
export function isLr0ChosenByOptimizer(values: TrainingParameters): boolean {
    return (values.optimizer ?? "AdamW") === "auto";
}

const finite = (value: unknown): value is number => typeof value === "number" && Number.isFinite(value);
const integer = (value: unknown, min: number, max = Number.MAX_SAFE_INTEGER) => finite(value) && Number.isInteger(value) && value >= min && value <= max;

/** Та же проверка, что в Core; false — поле подсвечивается («Проверьте значение параметра»). */
export function isValidTrainingParameter(key: string, value: unknown): boolean {
    const parameter = byKey.get(key);
    if (!parameter) return false;
    if (value === null) return parameter.nullable === true;
    switch (key) {
        case "batch":
            return finite(value) && (value === -1 || (value > 0 && value < 1) || (Number.isInteger(value) && value >= 1 && value <= 100_000));
        case "device":
            return typeof value === "string" && /^(cpu|-1|[0-9]{1,3})$/.test(value);
        case "amp":
            return typeof value === "boolean" || value === "fp16" || value === "bf16" || value === "fp32";
        case "cache":
            return typeof value === "boolean" || value === "ram" || value === "disk";
        case "channelsLast":
            return typeof value === "boolean";
        case "fraction": {
            const items = Array.isArray(value) ? value : [value];
            return items.length >= 1 && items.length <= 3 && items.every((item, index) => finite(item)
                && ((item === 0 && index === 2) || (item > 0 && item <= 1) || (item > 1 && Number.isInteger(item))));
        }
        case "freeze":
            return integer(value, 0, 10_000) || (Array.isArray(value) && value.every(item => integer(item, 0, 10_000)
                || (typeof item === "string" && /^[0-9]+(\.[A-Za-z0-9_]+)*$/.test(item))));
        case "classes":
            return Array.isArray(value) && value.length > 0 && value.every(item => integer(item, 0, 100_000)) && new Set(value).size === value.length;
        case "scale":
            return (finite(value) && value >= 0)
                || (Array.isArray(value) && value.length === 2 && value.every(item => finite(item) && item > 0) && (value[0] as number) <= (value[1] as number));
    }
    switch (parameter.type) {
        case "boolean":
            return typeof value === "boolean";
        case "select":
            return (parameter.choices ?? []).some(choice => choice.value === value);
        case "integer":
            return integer(value, parameter.min ?? Number.MIN_SAFE_INTEGER, parameter.max);
        case "number":
            return finite(value) && (parameter.exclusiveMin ? value > (parameter.min ?? -Infinity) : value >= (parameter.min ?? -Infinity))
                && value <= (parameter.max ?? Infinity);
        default:
            return false;
    }
}

/** Ключи с неверными значениями (пусто — можно отправлять). */
export function invalidTrainingParameters(values: TrainingParameters): string[] {
    return Object.entries(values).filter(([key, value]) => !isValidTrainingParameter(key, value)).map(([key]) => key);
}

/**
 * Поле list из текста: «5» → 5, «0, 2» → [0, 2], для freeze — и имена модулей «23.cv2»; пустой текст — null.
 * undefined — текст не разбирается (поле подсвечивается).
 */
export function parseListField(key: "freeze" | "fraction" | "classes" | "scale", text: string): TrainingParameterValue | undefined {
    const trimmed = text.trim();
    if (!trimmed) return key === "freeze" || key === "classes" ? null : undefined;
    const parts = trimmed.split(",").map(part => part.trim());
    if (parts.some(part => !part)) return undefined;
    const items = parts.map(part => (Number.isFinite(Number(part)) ? Number(part) : part));
    if (key !== "freeze" && items.some(item => typeof item !== "number")) return undefined;
    const value = items.length === 1 && key !== "classes" ? items[0] : items;
    return isValidTrainingParameter(key, value) ? value as TrainingParameterValue : undefined;
}

export interface TrainingDeviceOption {
    value: string;
    label: string;
}

/** «Устройство»: «Процессор», «Видеокарта N — имя, VRAM», «Автовыбор свободной видеокарты»; без драйвера — только процессор и ручной ввод. */
export function deviceOptions(inventory: GpuInventoryResponseDto | null): { options: TrainingDeviceOption[]; manualEntry: boolean } {
    const options: TrainingDeviceOption[] = [{ value: "cpu", label: "Процессор" }];
    if (!inventory?.driverAvailable || inventory.gpus.length === 0) return { options, manualEntry: true };
    for (const gpu of inventory.gpus) {
        options.push({ value: String(gpu.index), label: `Видеокарта ${gpu.index} — ${gpu.name}, ${(gpu.memoryTotalMb / 1024).toFixed(gpu.memoryTotalMb % 1024 === 0 ? 0 : 1)} ГБ` });
    }
    options.push({ value: "-1", label: "Автовыбор свободной видеокарты" });
    return { options, manualEntry: false };
}

/** Устройство по умолчанию: первая видеокарта, без них — процессор. */
export function defaultDevice(inventory: GpuInventoryResponseDto | null): string {
    return inventory?.driverAvailable && inventory.gpus.length > 0 ? String(inventory.gpus[0].index) : "cpu";
}

/** Датасет для предупреждений формы: состояние, совпадение с входом проекта, кадры по частям. */
export interface TrainingFormDataset {
    integrityState: "unverified" | "verified" | "corrupt";
    inputSizeMatches: boolean;
    counts: { train: number; val: number; test: number };
}

export interface TrainingFormWarning {
    code: "dataset_corrupt" | "input_size_mismatch" | "batch_exceeds_frames" | "no_validation";
    message: string;
    /** blocking — «Начать обучение» недоступна. */
    blocking: boolean;
}

/** Предупреждения формы. */
export function trainingFormWarnings(dataset: TrainingFormDataset, values: TrainingParameters): TrainingFormWarning[] {
    const warnings: TrainingFormWarning[] = [];
    if (dataset.integrityState === "corrupt") {
        warnings.push({ code: "dataset_corrupt", blocking: true, message: "Файлы датасета не прошли проверку. Соберите новую версию из исходников." });
    }
    if (!dataset.inputSizeMatches) {
        warnings.push({ code: "input_size_mismatch", blocking: true, message: "Размер входа датасета не совпадает с настройками проекта. Соберите датасет заново." });
    }
    const batch = values.batch;
    if (typeof batch === "number" && batch >= 1 && batch > dataset.counts.train) {
        warnings.push({ code: "batch_exceeds_frames", blocking: false,
            message: `Размер пакета (${batch}) больше числа обучающих кадров (${dataset.counts.train}): уменьшите batch.` });
    }
    if (dataset.counts.val === 0) {
        warnings.push({ code: "no_validation", blocking: false, message: "В датасете нет кадров Validation: качество модели не будет проверено." });
    }
    return warnings;
}

/** Подписи состояний запуска — одни на всех экранах. */
export const trainingRunStateLabels: Readonly<Record<TrainingRunState, string>> = {
    queued: "Ожидает видеокарту",
    preparing: "Подготовка",
    training: "Обучение",
    cancelling: "Отмена…",
    succeeded: "Готово",
    failed: "Ошибка",
    cancelled: "Отменено",
    interrupted: "Прервано",
};

/** Активный запуск: можно «Отменить обучение». */
export function isActiveTrainingRun(state: TrainingRunState): boolean {
    return state === "queued" || state === "preparing" || state === "training" || state === "cancelling";
}

/** Подписи стадий worker для «Текущий запуск». */
export const trainingStageLabels: Readonly<Record<string, string>> = {
    validating: "Проверка датасета",
    loading_model: "Загрузка модели",
    training: "Обучение",
    evaluating: "Проверка качества",
    finalizing: "Сохранение весов",
    exporting: "Экспорт ONNX",
};

/** Подписи метрик и потерь эпох (графики «Качество по эпохам» и «Потери по эпохам»). */
export const trainingMetricLabels: Readonly<Record<string, string>> = {
    precision: "Precision",
    recall: "Recall",
    mAP50: "mAP50",
    mAP50_95: "mAP50-95",
    box_loss: "Потери рамок (train)",
    cls_loss: "Потери классов (train)",
    dfl_loss: "Потери DFL (train)",
    l1_loss: "Потери L1 (train, YOLO26)",
    valBoxLoss: "Потери рамок (val)",
    valClsLoss: "Потери классов (val)",
    valDflLoss: "Потери DFL (val)",
};
