import { addSectionCatalogs } from "@/components/catalog/locale";
import en from "@/locales/en/backgrounds.json";
import ru from "@/locales/ru/backgrounds.json";

/** Texts of the backgrounds section (sets, gallery, imports, video and blur); they load with the chunk of the section. */
addSectionCatalogs("backgrounds", { ru, en });
