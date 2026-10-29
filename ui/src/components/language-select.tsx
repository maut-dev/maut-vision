import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { Languages } from "lucide-react";
import { subscribeLanguageChanged } from "@/api/events/subscriptions";
import type { LanguageSetting } from "@/api/settings/request-dto/language-request-dto";
import { settingsApi } from "@/api/settings/settings-api";
import { Select, SelectContent, SelectItem, SelectTrigger } from "@/components/ui/select";
import { applyLanguage } from "@/i18n";
import { errorMessage } from "@/lib/format";

const settings: readonly LanguageSetting[] = ["system", "ru", "en"];

/**
 * The interface language in the header: System, Russian, English. The host keeps the choice (host-settings.json) and relabels its
 * tray; the page switches its catalogs at once, without a reload.
 */
export function LanguageSelect({ disabled, failed }: { disabled?: boolean; failed: (message: string) => void }) {
    const { t } = useTranslation();
    const [setting, setSetting] = useState<LanguageSetting>();
    const [busy, setBusy] = useState(false);

    useEffect(() => {
        let alive = true;
        settingsApi.getLanguage().then(value => { if (alive) setSetting(value.setting); }, () => { /* the page outside the application */ });
        const unsubscribe = subscribeLanguageChanged(event => setSetting(event.data.setting));
        return () => {
            alive = false;
            unsubscribe();
        };
    }, []);

    async function choose(next: LanguageSetting) {
        setBusy(true);
        try {
            const result = await settingsApi.setLanguage({ language: next });
            setSetting(result.setting);
            await applyLanguage(result.language);
        } catch (cause) {
            failed(errorMessage(cause));
        } finally {
            setBusy(false);
        }
    }

    const label = t("language.label");
    return (
        <Select value={setting ?? ""} onValueChange={value => void choose(value as LanguageSetting)} disabled={disabled || busy || !setting}>
            <SelectTrigger size="sm" aria-label={label} title={label} className="border-0 px-2 shadow-none dark:bg-transparent" data-testid="language-select">
                <Languages aria-hidden />
            </SelectTrigger>
            <SelectContent position="popper" align="end">
                {settings.map(item => <SelectItem key={item} value={item} data-testid={`language-${item}`}>{t(`language.${item}`)}</SelectItem>)}
            </SelectContent>
        </Select>
    );
}
