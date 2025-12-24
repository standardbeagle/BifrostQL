import { Component, ErrorInfo, ReactNode } from 'react';

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

        // Log error with context
        console.error(`Error in ${section ?? 'component'}:`, error, errorInfo);

        // Call optional error handler
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
                <div className="editdb-error-boundary">
                    <div className="editdb-error-boundary__content">
                        <h3 className="editdb-error-boundary__title">
                            Something went wrong{section ? ` in ${section}` : ''}
                        </h3>
                        <p className="editdb-error-boundary__message">
                            {error?.message ?? 'An unexpected error occurred'}
                        </p>
                        <button
                            className="editdb-error-boundary__retry"
                            onClick={this.handleRetry}
                        >
                            Try Again
                        </button>
                    </div>
                </div>
            );
        }

        return children;
    }
}
