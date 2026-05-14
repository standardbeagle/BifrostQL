import { describe, it, expect } from 'vitest';
import type { EntityMetadata } from '@bifrostql/app-shell';
import { getSavedViewOptions } from './saved-views';

describe('getSavedViewOptions', () => {
  it('returns one option per saved view in declaration order', () => {
    // Arrange
    const entity: EntityMetadata = {
      fields: { status: { widget: 'select' } },
      grid: {
        savedViews: {
          active: { name: 'Active Members', filters: ['status = active'] },
          inactive: { name: 'Inactive Members', filters: ['status = inactive'] },
        },
      },
    };

    // Act
    const options = getSavedViewOptions(entity);

    // Assert
    expect(options.map((o) => o.id)).toEqual(['active', 'inactive']);
    expect(options.map((o) => o.name)).toEqual([
      'Active Members',
      'Inactive Members',
    ]);
  });

  it('builds an _eq table filter from a field = value expression', () => {
    // Arrange
    const entity: EntityMetadata = {
      fields: { status: { widget: 'select' } },
      grid: {
        savedViews: {
          inactive: { name: 'Inactive Members', filters: ['status = inactive'] },
        },
      },
    };

    // Act
    const [option] = getSavedViewOptions(entity);

    // Assert
    expect(option.filter).toEqual({ status: { _eq: 'inactive' } });
  });

  it('falls back to the view id when the overlay omits a name', () => {
    // Arrange
    const entity: EntityMetadata = {
      fields: {},
      grid: { savedViews: { active: { filters: ['status = active'] } } },
    };

    // Act
    const [option] = getSavedViewOptions(entity);

    // Assert
    expect(option.name).toBe('active');
  });

  it('ignores non-equality expressions, yielding an empty filter', () => {
    // Arrange
    const entity: EntityMetadata = {
      fields: {},
      grid: {
        savedViews: {
          'renewal-upcoming': {
            name: 'Renewal Upcoming',
            filters: ['end_date >= now', 'end_date <= now+30d'],
          },
        },
      },
    };

    // Act
    const [option] = getSavedViewOptions(entity);

    // Assert
    expect(option.filter).toEqual({});
  });

  it('returns no options when the entity declares no saved views', () => {
    // Arrange / Act / Assert
    expect(getSavedViewOptions(undefined)).toEqual([]);
    expect(getSavedViewOptions({ fields: {}, grid: {} })).toEqual([]);
  });
});
