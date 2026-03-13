import { Link, usePath } from './hooks/usePath';
import { useSchema } from './hooks/useSchema';
import { Table } from './types/schema';

export function TableList() {
    const {loading, error, data} = useSchema();
    const currentPath = usePath();
    const activeTable = currentPath.split('/')[1] ?? '';

    if (loading) return <div className="editdb-nav-loading">
        <div className="editdb-nav-loading__spinner" />
        <span>Loading schema…</span>
    </div>;

    if (error) return <div className="editdb-nav-error" role="alert">
        <span className="editdb-nav-error__icon">⚠</span>
        <span className="editdb-nav-error__title">Connection Error</span>
        <span className="editdb-nav-error__detail">{error.message}</span>
    </div>;

    if (data.length === 0) return <div className="editdb-nav-empty" role="status">
        <span className="editdb-nav-empty__icon">∅</span>
        <span className="editdb-nav-empty__title">No tables found</span>
        <span className="editdb-nav-empty__detail">
            The database returned an empty schema. The connection may be invalid or the database may have been removed.
        </span>
    </div>;

    return <nav className="editdb-nav" aria-label="Tables">
        <ul className="editdb-nav__list">
            {data.map((t: Table) => {
                const isActive = t.name === activeTable;
                return <li key={t.name} className="editdb-nav__item">
                    <Link
                        to={`/${t.name}`}
                        className={`plain-link editdb-nav__link${isActive ? ' active' : ''}`}
                        aria-current={isActive ? 'page' : undefined}
                        data-active={isActive ? 'true' : undefined}
                    >
                        {t.label}
                    </Link>
                </li>;
            })}
        </ul>
    </nav>
}
