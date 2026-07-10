import { Editor } from './editor';

export default Editor;
export { Editor };
export type { GraphQLFetcher } from './common/fetcher';
export { HttpGraphQLFetcher } from './common/fetcher';
export { useTableMutation } from './hooks/useTableMutation';
export type { UseTableMutationResult } from './hooks/useTableMutation';
export {
  createSavedObjectsClient,
  parseSavedObject,
  SavedObjectConflictError,
  SAVED_OBJECTS_PATH,
} from './common/saved-objects';
export type {
  SavedObject,
  SavedObjectType,
  SavedObjectsClient,
} from './common/saved-objects';
