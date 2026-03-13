import { useRouteError } from "./hooks/usePath";
import { Alert, AlertTitle, AlertDescription } from '@/components/ui/alert';
import { AlertCircle } from 'lucide-react';

export default function ErrorPage() {
  const error: any = useRouteError();

  return (
    <div className="flex items-center justify-center p-10">
      <Alert variant="destructive" className="max-w-md">
        <AlertCircle className="size-4" />
        <AlertTitle>Oops!</AlertTitle>
        <AlertDescription>
          <p>Sorry, an unexpected error has occurred.</p>
          <p className="mt-1 italic">{error.statusText || error.message}</p>
        </AlertDescription>
      </Alert>
    </div>
  );
}
