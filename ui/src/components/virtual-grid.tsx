import { useTranslation } from "react-i18next";
import { Fragment, useEffect, useLayoutEffect, useState, type CSSProperties, type ReactNode } from "react";

const gap = 12;
const overscanRows = 3;

/** Строки сетки, попадающие в окно прокрутки, с запасом; остальные плитки не создаются. */
export function visibleRows(scrollTop: number, viewportHeight: number, rows: number, rowHeight: number) {
    const first = Math.max(0, Math.floor(scrollTop / rowHeight) - overscanRows);
    const last = Math.min(rows - 1, Math.ceil((scrollTop + viewportHeight) / rowHeight) + overscanRows);
    return { first, last };
}

/**
 * Виртуальная сетка плиток фиксированной высоты: рисуются только видимые строки, поэтому список на сотни тысяч элементов
 * не создаёт сотни тысяч узлов. Следующая страница подгружается, когда до конца загруженного остаётся пара строк.
 * Общая для галерей изображений и фонов; плитку рисует владелец.
 */
export function VirtualGrid<T>({ items, keyOf, minTileWidth, tileHeight, hasMore, loading, loadMore, label, testId, loadingText, renderTile, className = "min-h-0" }: {
    items: readonly T[];
    keyOf: (item: T) => string;
    minTileWidth: number;
    tileHeight: number;
    hasMore: boolean;
    loading: boolean;
    loadMore: () => void;
    label: string;
    testId: string;
    /** «Загрузка изображений…» и т. п. */
    loadingText: string;
    renderTile: (item: T, index: number, style: CSSProperties) => ReactNode;
    /** Высота сетки внутри колонки, по умолчанию сжимается до нуля. */
    className?: string;
}) {
    const [element, setElement] = useState<HTMLDivElement | null>(null);
    const [view, setView] = useState({ top: 0, width: 800, height: 600 });
    const rowHeight = tileHeight + gap;
    const columns = Math.max(1, Math.floor((view.width + gap) / (minTileWidth + gap)));
    const rows = Math.ceil(items.length / columns);
    const { first, last } = visibleRows(view.top, view.height, rows, rowHeight);
    const tileWidth = (view.width - gap * (columns - 1)) / columns;

    useLayoutEffect(() => {
        if (!element) return;
        const measure = () => setView({ top: element.scrollTop, width: element.clientWidth, height: element.clientHeight });
        measure();
        const observer = new ResizeObserver(measure);
        observer.observe(element);
        const scroll = () => setView(value => value.top === element.scrollTop ? value : { ...value, top: element.scrollTop });
        element.addEventListener("scroll", scroll, { passive: true });
        return () => {
            observer.disconnect();
            element.removeEventListener("scroll", scroll);
        };
    }, [element]);

    useEffect(() => {
        if (hasMore && !loading && last >= rows - 2) loadMore();
    }, [hasMore, loading, last, rows, loadMore]);

    const tiles: ReactNode[] = [];
    for (let row = first; row <= last; row++) {
        for (let column = 0; column < columns; column++) {
            const index = row * columns + column;
            const item = items[index];
            if (item === undefined) break;
            tiles.push(
                <Fragment key={keyOf(item)}>
                    {renderTile(item, index, { position: "absolute", left: column * (tileWidth + gap), top: row * rowHeight, width: tileWidth, height: tileHeight })}
                </Fragment>,
            );
        }
    }

    return (
        <div
            ref={setElement}
            className={`${className} flex-1 overflow-y-auto overscroll-contain`}
            role="region"
            aria-label={label}
            data-testid={testId}
            data-loaded={items.length}
        >
            <div className="relative" style={{ height: Math.max(0, rows * rowHeight - gap) }}>{tiles}</div>
            {hasMore && <p role="status" className="py-3 text-center text-xs text-muted-foreground">{loading ? loadingText : <ScrollMoreText />}</p>}
        </div>
    );
}

function ScrollMoreText() {
    const { t } = useTranslation();
    return t("grid.scrollMore");
}
