import { describe, it, expect } from 'vitest';
import { getEmailSegmentOptions } from './segment-definitions';
import type { AppMetadataWithSegments } from './segment-definitions';

describe('getEmailSegmentOptions', () => {
  it('returns one option per email segment in declaration order', () => {
    // Arrange
    const metadata: AppMetadataWithSegments = {
      entities: {},
      emailSegments: {
        'active-members': {
          name: 'Active Members',
          entity: 'main.members',
          filters: ['status = active'],
        },
        'lapsed-members': {
          name: 'Lapsed Members',
          entity: 'main.members',
          filters: ['status = inactive'],
        },
      },
    };

    // Act
    const options = getEmailSegmentOptions(metadata);

    // Assert
    expect(options.map((o) => o.id)).toEqual([
      'active-members',
      'lapsed-members',
    ]);
    expect(options.map((o) => o.name)).toEqual([
      'Active Members',
      'Lapsed Members',
    ]);
  });

  it('carries the segment entity key through to the option', () => {
    // Arrange
    const metadata: AppMetadataWithSegments = {
      entities: {},
      emailSegments: {
        'active-members': {
          name: 'Active Members',
          entity: 'main.members',
          filters: ['status = active'],
        },
      },
    };

    // Act
    const [option] = getEmailSegmentOptions(metadata);

    // Assert
    expect(option.entityKey).toBe('main.members');
  });

  it('builds an _eq table filter from a field = value expression', () => {
    // Arrange
    const metadata: AppMetadataWithSegments = {
      entities: {},
      emailSegments: {
        'active-members': {
          name: 'Active Members',
          entity: 'main.members',
          filters: ['status = active'],
        },
      },
    };

    // Act
    const [option] = getEmailSegmentOptions(metadata);

    // Assert
    expect(option.filter).toEqual({ status: { _eq: 'active' } });
  });

  it('falls back to the segment id when the overlay omits a name', () => {
    // Arrange
    const metadata: AppMetadataWithSegments = {
      entities: {},
      emailSegments: {
        'active-members': {
          entity: 'main.members',
          filters: ['status = active'],
        },
      },
    };

    // Act
    const [option] = getEmailSegmentOptions(metadata);

    // Assert
    expect(option.name).toBe('active-members');
  });

  it('ignores non-equality expressions, yielding an empty filter', () => {
    // Arrange
    const metadata: AppMetadataWithSegments = {
      entities: {},
      emailSegments: {
        'renewal-soon': {
          name: 'Renewal Soon',
          entity: 'main.member_memberships',
          filters: ['end_date >= now', 'end_date <= now+30d'],
        },
      },
    };

    // Act
    const [option] = getEmailSegmentOptions(metadata);

    // Assert
    expect(option.filter).toEqual({});
  });

  it('drops a segment whose entity key is blank', () => {
    // Arrange
    const metadata: AppMetadataWithSegments = {
      entities: {},
      emailSegments: {
        broken: { name: 'No Entity', entity: '', filters: ['status = active'] },
      },
    };

    // Act / Assert
    expect(getEmailSegmentOptions(metadata)).toEqual([]);
  });

  it('returns no options when the overlay declares no email segments', () => {
    // Arrange / Act / Assert
    expect(getEmailSegmentOptions(undefined)).toEqual([]);
    expect(getEmailSegmentOptions({ entities: {} })).toEqual([]);
  });
});
