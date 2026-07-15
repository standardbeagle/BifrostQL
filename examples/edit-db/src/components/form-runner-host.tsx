/**
 * Self-contained host for {@link FormRunner}. Provides the same context stack the
 * Editor sets up (React Query, the GraphQL fetcher, toasts, and the schema
 * loader) so a caller outside the Editor — e.g. the desktop shell's forms pane —
 * can run a saved form with only a fetcher and a definition, without mounting the
 * whole Editor. It deliberately reuses the SAME fetcher seam, so the shell's
 * HTTP<->binary transport toggle covers the runner too.
 */

import { useMemo } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { FetcherProvider, type GraphQLFetcher } from '../common/fetcher';
import { SchemaProvider } from '../hooks/useSchema';
import { ToastProvider } from '../hooks/useToast';
import { FormRunner } from './form-runner';
import type { FormDefinition } from '../lib/form-definition';
import type { FieldWidgetHint } from '../lib/form-widget';

interface FormRunnerHostProps {
  /** The GraphQL fetcher every read/write routes through (transport-agnostic). */
  fetcher: GraphQLFetcher;
  /** The saved form definition to render. */
  definition: FormDefinition;
  /** Optional app-metadata widget hints keyed by column name. */
  fieldMetadata?: Record<string, FieldWidgetHint>;
  onClose?: () => void;
}

export function FormRunnerHost({ fetcher, definition, fieldMetadata, onClose }: FormRunnerHostProps) {
  const queryClient = useMemo(
    () => new QueryClient({ defaultOptions: { queries: { staleTime: 5 * 60 * 1000, retry: 1 } } }),
    [],
  );

  return (
    <QueryClientProvider client={queryClient}>
      <FetcherProvider value={fetcher}>
        <ToastProvider>
          <SchemaProvider>
            <FormRunner definition={definition} fieldMetadata={fieldMetadata} onClose={onClose} />
          </SchemaProvider>
        </ToastProvider>
      </FetcherProvider>
    </QueryClientProvider>
  );
}
