import { addSectionCatalogs } from "@/components/catalog/locale";
import en from "@/locales/en/images.json";
import ru from "@/locales/ru/images.json";

/** Texts of the images section (gallery, annotation editor, imports); they load with the chunk of the section. */
addSectionCatalogs("images", { ru, en });
