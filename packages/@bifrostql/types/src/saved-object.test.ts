import { describe, it, expectTypeOf } from 'vitest';
import type { SavedObject, SavedObjectType } from './index';

/**
 * Type-level contract for the saved-object types re-exported from the barrel.
 * A rename or shape drift fails the typecheck rather than a runtime assertion.
 */
describe('@bifrostql/types saved-object contract', () => {
  it('exposes the SavedObjectType union', () => {
    expectTypeOf<SavedObjectType>().toEqualTypeOf<'query' | 'form' | 'report' | 'dashboard'>();
  });

  it('exposes the SavedObject shape with an optional folder and opaque definition', () => {
    expectTypeOf<SavedObject>().toHaveProperty('id');
    expectTypeOf<SavedObject>().toHaveProperty('type');
    expectTypeOf<SavedObject>().toHaveProperty('version');

    const obj: SavedObject = {
      id: 'q1',
      type: 'query',
      name: 'Sales',
      definition: { groupBy: ['region'] },
      version: 1,
    };
    expectTypeOf(obj).toMatchTypeOf<SavedObject>();
    // folder is optional.
    expectTypeOf(obj.folder).toEqualTypeOf<string | undefined>();
  });
});
