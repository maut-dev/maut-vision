import { i18n } from "@/i18n";
import { registerSection } from "@/i18n/sections";
import en from "@/locales/en/training.json";
import ru from "@/locales/ru/training.json";

/** Texts of training; sections that borrow parameter labels or device names import it too. */
registerSection("training", { ru, en });

/** A text of the training catalog by a key built at run time (a parameter, a stage); keys written in code use the typed t. */
export const trainingText = (key: string, options?: Record<string, unknown>): string =>
    (i18n.t.bind(i18n) as unknown as (key: string, options?: Record<string, unknown>) => string)(`training:${key}`, options);
