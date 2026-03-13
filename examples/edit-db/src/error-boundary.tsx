import { Component, ErrorInfo, ReactNode } from 'react';
import { Alert, AlertTitle, AlertDescription } from '@/components/ui/alert';
import { AlertCircle } from 'lucide-react';
import { Button } from '@/components/ui/button';

interface ErrorBoundaryProps {
    children: ReactNode;
    fallback?: ReactNode;
    onError?: (error: Error, errorInfo: ErrorInfo) => void;
    section?: string;
}

interface ErrorBoundaryState {
    hasError: boolean;
    error: Error | null;
}

export class ErrorBoundary extends Component<ErrorBoundaryProps, ErrorBoundaryState> {
    constructor(props: ErrorBoundaryProps) {
        super(props);
        this.state = { hasError: false, error: null };
    }

    static getDerivedStateFromError(error: Error): ErrorBoundaryState {
        return { hasError: true, error };
    }

    componentDidCatch(error: Error, errorInfo: ErrorInfo): void {
        const { onError, section } = this.props;
        console.error(`Error in ${section ?? 'component'}:`, error, errorInfo);
        onError?.(error, errorInfo);
    }

    handleRetry = (): void => {
        this.setState({ hasError: false, error: null });
    };

    render(): ReactNode {
        const { hasError, error } = this.state;
        const { children, fallback, section } = this.props;

        if (hasError) {
            if (fallback) {
                return fallback;
            }

            return (
                <div className="flex items-center justify-center p-5 min-h-[100px]">
                    <Alert variant="destructive" className="max-w-[400px]">
                        <AlertCircle className="size-4" />
                        <AlertTitle>
                            Something went wrong{section ? ` in ${section}` : ''}
                        </AlertTitle>
                        <AlertDescription className="mt-2">
                            <p>{error?.message ?? 'An unexpected error occurred'}</p>
                            <Button
                                variant="destructive"
                                size="sm"
                                className="mt-3"
                                onClick={this.handleRetry}
                            >
                                Try Again
                            </Button>
                        </AlertDescription>
                    </Alert>
                </div>
            );
        }

        return children;
    }
}
