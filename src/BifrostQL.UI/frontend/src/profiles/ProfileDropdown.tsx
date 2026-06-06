import type { ApiProfile } from './types';

interface ProfileDropdownProps {
  profiles: ApiProfile[];
  activeId: string;
  onSelect: (id: string) => void;
}

/**
 * Header profile picker. Always rendered; disabled (greyed, with a tooltip)
 * when the connection exposes only a single profile.
 */
export function ProfileDropdown({ profiles, activeId, onSelect }: ProfileDropdownProps) {
  const disabled = profiles.length <= 1;
  return (
    <label
      className="bifrost-profile-picker"
      style={{
        display: 'flex',
        alignItems: 'center',
        gap: 6,
        marginRight: 12,
        fontSize: 12,
      }}
      title={disabled ? 'This connection exposes a single profile' : undefined}
    >
      <span>Profile</span>
      <select
        aria-label="Profile"
        value={activeId}
        disabled={disabled}
        onChange={(e) => onSelect(e.target.value)}
        style={{
          background: 'transparent',
          color: 'inherit',
          border: '1px solid currentColor',
          borderRadius: 4,
          padding: '2px 8px',
          font: 'inherit',
          fontSize: 12,
          cursor: disabled ? 'not-allowed' : 'pointer',
          opacity: disabled ? 0.5 : 1,
        }}
      >
        {profiles.map((p) => (
          <option key={p.id} value={p.id}>
            {p.label}
          </option>
        ))}
      </select>
    </label>
  );
}
