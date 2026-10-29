import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { blurForm, defaultBlurFields, type BlurFields } from "./backgrounds/blur-form";

/** "Blur backgrounds" is shared by all ways of adding: the range is not reset when the way changes. */
export function useBackgroundBlur() {
    const [fields, setFields] = useState<BlurFields>(defaultBlurFields);
    const { i18n } = useTranslation();
    // The language is a dependency: the messages are texts of the current language.
    const form = useMemo(() => blurForm(fields), [fields, i18n.language]);
    return {
        fields,
        change: (patch: Partial<BlurFields>) => setFields(current => ({ ...current, ...patch })),
        ...form,
        valid: !form.error,
        /** A key that drops stale samples when the blur changes. */
        key: form.value ? JSON.stringify(form.value) : fields.enabled ? "invalid" : "off",
    };
}

export type BackgroundBlurState = ReturnType<typeof useBackgroundBlur>;
