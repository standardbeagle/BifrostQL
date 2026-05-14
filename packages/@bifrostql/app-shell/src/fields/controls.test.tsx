import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import {
  ScalarControl,
  DateControl,
  BooleanControl,
  EnumSelectControl,
  JsonTextControl,
  FkLookupControl,
} from './controls';

describe('ScalarControl', () => {
  it('emits the next string value on change', () => {
    // Arrange
    const onChange = vi.fn();
    render(
      <ScalarControl name="n" label="N" value="a" onChange={onChange} />,
    );

    // Act
    fireEvent.change(screen.getByLabelText('N'), { target: { value: 'ab' } });

    // Assert
    expect(onChange).toHaveBeenCalledWith('ab');
  });

  it('renders an empty string for null values', () => {
    // Arrange / Act
    render(
      <ScalarControl name="n" label="N" value={null} onChange={vi.fn()} />,
    );

    // Assert
    expect(screen.getByLabelText('N')).toHaveValue('');
  });
});

describe('DateControl', () => {
  it('emits the next date string on change', () => {
    // Arrange
    const onChange = vi.fn();
    render(
      <DateControl name="d" label="D" value="" onChange={onChange} />,
    );

    // Act
    fireEvent.change(screen.getByLabelText('D'), {
      target: { value: '2026-05-14' },
    });

    // Assert
    expect(onChange).toHaveBeenCalledWith('2026-05-14');
  });
});

describe('BooleanControl', () => {
  it('emits the checked state on toggle', () => {
    // Arrange
    const onChange = vi.fn();
    render(
      <BooleanControl name="b" label="B" value={true} onChange={onChange} />,
    );

    // Act
    fireEvent.click(screen.getByLabelText('B'));

    // Assert
    expect(onChange).toHaveBeenCalledWith(false);
  });
});

describe('EnumSelectControl', () => {
  it('emits the selected option value', () => {
    // Arrange
    const onChange = vi.fn();
    render(
      <EnumSelectControl
        name="s"
        label="S"
        value="open"
        options={['open', 'closed']}
        onChange={onChange}
      />,
    );

    // Act
    fireEvent.change(screen.getByLabelText('S'), {
      target: { value: 'closed' },
    });

    // Assert
    expect(onChange).toHaveBeenCalledWith('closed');
  });

  it('includes a blank placeholder option', () => {
    // Arrange / Act
    render(
      <EnumSelectControl
        name="s"
        label="S"
        value=""
        options={['x']}
        onChange={vi.fn()}
      />,
    );

    // Assert: the placeholder plus one real option.
    expect(screen.getAllByRole('option')).toHaveLength(2);
  });
});

describe('JsonTextControl', () => {
  it('emits raw text on change', () => {
    // Arrange
    const onChange = vi.fn();
    render(
      <JsonTextControl name="j" label="J" value="" onChange={onChange} />,
    );

    // Act
    fireEvent.change(screen.getByLabelText('J'), {
      target: { value: '{"a":1}' },
    });

    // Assert
    expect(onChange).toHaveBeenCalledWith('{"a":1}');
  });

  it('shows a Format button for valid JSON and pretty-prints on click', () => {
    // Arrange
    const onChange = vi.fn();
    render(
      <JsonTextControl
        name="j"
        label="J"
        value='{"a":1}'
        onChange={onChange}
      />,
    );

    // Act
    fireEvent.click(screen.getByTestId('field-j-format'));

    // Assert: the value is reformatted with indentation.
    expect(onChange).toHaveBeenCalledWith('{\n  "a": 1\n}');
  });

  it('hides the Format button for non-JSON values', () => {
    // Arrange / Act
    render(
      <JsonTextControl
        name="j"
        label="J"
        value="not json"
        onChange={vi.fn()}
      />,
    );

    // Assert
    expect(screen.queryByTestId('field-j-format')).not.toBeInTheDocument();
  });
});

describe('FkLookupControl', () => {
  it('emits the selected row key', () => {
    // Arrange
    const onChange = vi.fn();
    render(
      <FkLookupControl
        name="fk"
        label="FK"
        value=""
        targetEntity="dbo.users"
        options={[
          { key: '1', label: 'Alice' },
          { key: '2', label: 'Bob' },
        ]}
        onChange={onChange}
      />,
    );

    // Act
    fireEvent.change(screen.getByLabelText('FK'), { target: { value: '2' } });

    // Assert
    expect(onChange).toHaveBeenCalledWith('2');
  });
});
