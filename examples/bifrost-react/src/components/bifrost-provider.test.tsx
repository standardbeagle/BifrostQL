import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { useContext } from 'react';
import { BifrostProvider, BifrostContext } from './bifrost-provider';

function TestConsumer() {
  const config = useContext(BifrostContext);
  return <div data-testid="endpoint">{config?.endpoint ?? 'none'}</div>;
}

describe('BifrostProvider', () => {
  it('provides config to children via context', () => {
    render(
      <BifrostProvider config={{ endpoint: 'http://localhost:5000/graphql' }}>
        <TestConsumer />
      </BifrostProvider>,
    );
    expect(screen.getByTestId('endpoint')).toHaveTextContent(
      'http://localhost:5000/graphql',
    );
  });

  it('provides headers when configured', () => {
    function HeaderConsumer() {
      const config = useContext(BifrostContext);
      return (
        <div data-testid="auth">
          {config?.headers?.['Authorization'] ?? 'none'}
        </div>
      );
    }

    render(
      <BifrostProvider
        config={{
          endpoint: 'http://localhost/graphql',
          headers: { Authorization: 'Bearer test-token' },
        }}
      >
        <HeaderConsumer />
      </BifrostProvider>,
    );
    expect(screen.getByTestId('auth')).toHaveTextContent('Bearer test-token');
  });

  it('returns null when used outside provider', () => {
    render(<TestConsumer />);
    expect(screen.getByTestId('endpoint')).toHaveTextContent('none');
  });
});
