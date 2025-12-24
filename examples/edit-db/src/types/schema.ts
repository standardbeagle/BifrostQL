export interface TableMetadata {
  type?: {
    type: 'lookup';
    id: string;
    label: string;
  };
  [key: string]: any;
}

export interface LookupMetadata {
  type: 'lookup';
  id: string;
  label: string;
}

export interface ColumnMetadata {
  [key: string]: string | LookupMetadata | undefined;
}

export interface Column {
  dbName: string;
  graphQlName: string;
  name: string;
  label: string;
  paramType: string;
  isPrimaryKey: boolean;
  isIdentity: boolean;
  isNullable: boolean;
  isReadOnly: boolean;
  metadata: ColumnMetadata;
}

export interface Join {
  name: string;
  sourceColumnNames: string[];
  destinationTable: string;
  destinationColumnNames: string[];
}

export interface Table {
  dbName: string;
  graphQlName: string;
  name: string;
  label: string;
  labelColumn: string;
  primaryKeys: string[];
  isEditable: boolean;
  metadata: TableMetadata;
  columns: Column[];
  multiJoins: Join[];
  singleJoins: Join[];
}

export interface Schema {
  loading: boolean;
  error: { message: string} | null;
  data: Table[];
  findTable: (tableName: string) => Table | undefined;
}
