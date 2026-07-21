import { describe, expect, it } from 'vitest';
import type { ErdTable as Table } from './types';
import { layoutErd, mapSchemaToErd, neighborhood, schemaName } from './model';

function table(name: string, options: Record<string, unknown> = {}): Table {
  return { name, graphQlName: name, dbName: `dbo.${name}`, label: name, primaryKeys: ['id'], columns: [{ name: 'id', graphQlName: 'id', dbName: 'id', isPrimaryKey: true }], singleJoins: [], multiJoins: [], ...options } as unknown as Table;
}

describe('ER diagram schema mapping', () => {
  const users = table('users');
  const posts = table('posts', { singleJoins: [{ name: 'users', sourceColumnNames: ['userId'], destinationTable: 'users', destinationColumnNames: ['id'] }] });
  const tags = table('tags');
  const postTags = table('postTags');
  const comments = table('comments', { singleJoins: [{ name: 'commentable', sourceColumnNames: ['commentableId'], destinationTable: 'posts', destinationColumnNames: ['id'], isPolymorphic: true, polymorphicTypeColumn: 'commentableType', polymorphicTypeValue: 'post' }] });
  const aliases = table('aliases', { singleJoins: [{ name: 'users', sourceColumnNames: ['ownerId'], destinationTable: 'users', destinationColumnNames: ['id'], relationshipKind: 'name-based' }] });
  users.manyToManyJoins = [{ name: 'postTags', targetTable: 'tags', junctionTable: 'postTags', junctionTargetField: 'tag', sourceColumnNames: ['id'], junctionSourceColumnNames: ['postId'], junctionTargetColumnNames: ['tagId'], targetColumnNames: ['id'], hasPayload: false }];
  tags.manyToManyJoins = [{ ...users.manyToManyJoins[0], sourceColumnNames: ['id'], targetTable: 'users' }];

  it('maps FK, M2M, name-based, and polymorphic relationships without rendering a junction node', () => {
    const graph = mapSchemaToErd([users, posts, tags, postTags, comments, aliases]);
    expect(graph.edges.filter((edge) => edge.data?.kind === 'foreign-key')).toHaveLength(1);
    expect(graph.edges.filter((edge) => edge.data?.kind === 'many-to-many')).toHaveLength(1);
    expect(graph.nodes.map((node) => node.id)).not.toContain('postTags');
    expect(graph.edges.find((edge) => edge.data?.kind === 'name-based')?.style).toMatchObject({ strokeDasharray: '5 4' });
    expect(graph.edges.find((edge) => edge.data?.kind === 'polymorphic')?.label).toContain('polymorphic');
  });

  it('lays out 100 tables quickly and filters a N-hop neighborhood', async () => {
    const tables = Array.from({ length: 100 }, (_, index) => table(`t${index}`, { dbName: `${index < 50 ? 'sales' : 'ops'}.t${index}`, singleJoins: index ? [{ name: `t${index - 1}`, sourceColumnNames: ['id'], destinationTable: `t${index - 1}`, destinationColumnNames: ['id'] }] : [] }));
    const graph = mapSchemaToErd(tables);
    const started = performance.now();
    const laidOut = await layoutErd(graph);
    expect(performance.now() - started).toBeLessThan(3000);
    expect(neighborhood(laidOut, 't50', 1).nodes).toHaveLength(3);
    expect(laidOut.nodes.filter((node) => schemaName(node.data) === 'sales').length).toBeLessThan(laidOut.nodes.length);
  });
});
