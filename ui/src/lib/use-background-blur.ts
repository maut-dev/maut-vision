import { useMemo, useState } from "react";
import { blurForm, defaultBlurFields, type BlurFields } from "./backgrounds/blur-form";

/** «Размывать фоны» — общее для всех способов добавления: диапазон не сбрасывается при смене способа. */
export function useBackgroundBlur() {
    const [fields, setFields] = useState<BlurFields>(defaultBlurFields);
    const form = useMemo(() => blurForm(fields), [fields]);
    return {
        fields,
        change: (patch: Partial<BlurFields>) => setFields(current => ({ ...current, ...patch })),
        ...form,
        valid: !form.error,
        /** Ключ для сброса устаревших примеров при изменении размытия. */
        key: form.value ? JSON.stringify(form.value) : fields.enabled ? "invalid" : "off",
    };
}

export type BackgroundBlurState = ReturnType<typeof useBackgroundBlur>;
