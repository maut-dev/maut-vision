import type { ImageSplitHint } from "@/api/projects/images/response-dto/image-group-response-dto";
import { i18n } from "@/i18n";
import "@/i18n/sections/images";

/** Labels of the purposes of image groups in the current language (images and datasets): read during render, so a language change relabels them. */
export const splitHintLabels: Readonly<Record<ImageSplitHint, string>> = {
    get unassigned() { return i18n.t("images:splitHint.unassigned"); },
    get train() { return i18n.t("images:splitHint.train"); },
    get validation() { return i18n.t("images:splitHint.validation"); },
    get test() { return i18n.t("images:splitHint.test"); },
};
