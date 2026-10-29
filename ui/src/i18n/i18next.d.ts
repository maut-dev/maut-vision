import "i18next";
import type common from "@/locales/ru/common.json";
import type errors from "@/locales/ru/errors.json";
import type jobLabels from "@/locales/ru/jobLabels.json";
import type progress from "@/locales/ru/progress.json";
import type trainingLog from "@/locales/ru/trainingLog.json";
import type worker from "@/locales/ru/worker.json";
import type images from "@/locales/ru/images.json";
import type catalog from "@/locales/ru/catalog.json";
import type backgrounds from "@/locales/ru/backgrounds.json";
import type checks from "@/locales/ru/checks.json";
import type overview from "@/locales/ru/overview.json";
import type analytics from "@/locales/ru/analytics.json";
import type generation from "@/locales/ru/generation.json";
import type datasets from "@/locales/ru/datasets.json";
import type training from "@/locales/ru/training.json";
import type models from "@/locales/ru/models.json";
import type mcp from "@/locales/ru/mcp.json";

// Keys are typed by the Russian catalogs: a missing key fails the typecheck; scripts/check-locales.ts keeps English in step.
declare module "i18next" {
    interface CustomTypeOptions {
        defaultNS: "common";
        resources: {
            common: typeof common;
            errors: typeof errors;
            progress: typeof progress;
            trainingLog: typeof trainingLog;
            jobLabels: typeof jobLabels;
            worker: typeof worker;
            images: typeof images;
            catalog: typeof catalog;
            backgrounds: typeof backgrounds;
            checks: typeof checks;
            overview: typeof overview;
            analytics: typeof analytics;
            generation: typeof generation;
            datasets: typeof datasets;
            training: typeof training;
            models: typeof models;
            mcp: typeof mcp;
        };
        returnNull: false;
    }
}