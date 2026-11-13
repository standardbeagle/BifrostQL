import {
    columnFilteringFeature,
    columnResizingFeature,
    columnSizingFeature,
    columnVisibilityFeature,
    rowPaginationFeature,
    rowSelectionFeature,
    rowSortingFeature,
    tableFeatures,
} from '@tanstack/react-table';

/**
 * The TanStack Table features the data grid uses. Sorting, filtering and
 * pagination are all server-side (manual*), so no client row models are
 * registered — only the state and column/table APIs each feature adds.
 * Shared so column definitions, filters and headers type-check against the
 * same feature set the table is built with.
 */
export const dataTableFeatures = tableFeatures({
    columnFilteringFeature,
    columnSizingFeature,
    columnResizingFeature,
    columnVisibilityFeature,
    rowPaginationFeature,
    rowSelectionFeature,
    rowSortingFeature,
});

export type DataTableFeatures = typeof dataTableFeatures;
