/** Read-only subset of the existing edit-db `_dbSchema` introspection result. */
export interface ErdColumn { name: string; graphQlName?: string; dbName?: string; isPrimaryKey: boolean; }
export interface ErdJoin { name: string; fieldName?: string; sourceColumnNames: string[]; destinationTable: string; destinationColumnNames: string[]; isPolymorphic?: boolean; polymorphicTypeColumn?: string; polymorphicTypeValue?: string; relationshipKind?: 'foreign-key' | 'name-based' | 'polymorphic'; metadata?: Record<string, string>; }
export interface ErdManyToMany { name: string; targetTable: string; junctionTable: string; junctionTargetField: string; sourceColumnNames: string[]; junctionSourceColumnNames: string[]; junctionTargetColumnNames: string[]; targetColumnNames: string[]; hasPayload: boolean; }
export interface ErdTable { [key: string]: unknown; name: string; graphQlName: string; dbName: string; label: string; primaryKeys: string[]; columns: ErdColumn[]; singleJoins: ErdJoin[]; multiJoins: ErdJoin[]; manyToManyJoins?: ErdManyToMany[]; }

/** The raw, existing `_dbSchema` response before edit-db's client normalization. */
export interface ErdSchemaTable {
  graphQlName: string;
  dbName: string;
  labelColumn: string;
  primaryKeys: string[];
  columns: Array<Omit<ErdColumn, 'name'> & { graphQlName: string; dbName: string }>;
  singleJoins: ErdJoin[];
  multiJoins: ErdJoin[];
  manyToManyJoins?: ErdManyToMany[];
}
