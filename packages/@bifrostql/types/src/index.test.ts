import { describe, it, expectTypeOf } from 'vitest';
import type {
  AppMetadata,
  EntityMetadata,
  FieldMetadata,
  GridMetadata,
  SavedViewMetadata,
  RelationshipMetadata,
  RelationshipKind,
  TableFilter,
  FieldFilter,
  CompoundFilter,
  AdvancedFilter,
  PaginationOptions,
  SortOption,
  QueryOptions,
} from './index';

/**
 * The barrel has no runtime exports (types only), so this suite verifies the
 * contract at the type level: every expected name must be importable from the
 * barrel and resolve to its expected shape. A missing or renamed export fails
 * `tsc` / `vitest` typecheck rather than a runtime assertion.
 */
describe('@bifrostql/types barrel', () => {
  it('re-exports the app-metadata contract types', () => {
    expectTypeOf<RelationshipKind>().toEqualTypeOf<
      'foreignKeySelector' | 'childCollection' | 'nestedPanel'
    >();

    expectTypeOf<FieldMetadata>().toHaveProperty('widget');
    expectTypeOf<SavedViewMetadata>().toHaveProperty('columns');
    expectTypeOf<GridMetadata>().toHaveProperty('savedViews');
    expectTypeOf<RelationshipMetadata>().toHaveProperty('targetEntity');
    expectTypeOf<EntityMetadata>().toHaveProperty('relationships');
    expectTypeOf<AppMetadata>().toHaveProperty('entities');

    const meta: AppMetadata = {
      entities: {
        'dbo.users': {
          label: 'Users',
          fields: { name: { widget: 'text' } },
          grid: { defaultColumns: ['id', 'name'] },
          relationships: {
            orders: { targetEntity: 'sales.orders', kind: 'childCollection' },
          },
        },
      },
    };
    expectTypeOf(meta).toMatchTypeOf<AppMetadata>();
  });

  it('re-exports the filter/query contract types', () => {
    expectTypeOf<FieldFilter>().toHaveProperty('_eq');
    expectTypeOf<TableFilter>().toBeObject();
    expectTypeOf<CompoundFilter>().toHaveProperty('_and');
    expectTypeOf<AdvancedFilter>().toEqualTypeOf<
      TableFilter | CompoundFilter
    >();
    expectTypeOf<PaginationOptions>().toHaveProperty('limit');
    expectTypeOf<SortOption>().toHaveProperty('direction');
    expectTypeOf<QueryOptions>().toHaveProperty('filter');

    const opts: QueryOptions = {
      filter: { status: 'active', age: { _gte: 18 } },
      sort: [{ field: 'name', direction: 'asc' }],
      pagination: { limit: 25, offset: 0 },
      fields: ['id', 'name'],
    };
    expectTypeOf(opts).toMatchTypeOf<QueryOptions>();
  });
});
