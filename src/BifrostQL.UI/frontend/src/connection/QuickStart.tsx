import React, { useState } from 'react';
import { QuickStartSchema, DataSize, QuickStartProps } from './types';

interface SchemaInfo {
  id: QuickStartSchema;
  icon: string;
  name: string;
  description: string;
  features: string;
}

const SCHEMAS: SchemaInfo[] = [
  {
    id: 'blog',
    icon: '\u270D',
    name: 'Blog',
    description: 'Posts, authors, comments & tags',
    features: 'Many-to-many joins, nested comments, status filtering',
  },
  {
    id: 'ecommerce',
    icon: '\uD83D\uDED2',
    name: 'E-commerce',
    description: 'Products, orders, customers & reviews',
    features: 'Composite relationships, nullable FKs, numeric filtering',
  },
  {
    id: 'crm',
    icon: '\uD83E\uDD1D',
    name: 'CRM',
    description: 'Contacts, companies, deals & activities',
    features: 'Self-referencing hierarchy, polymorphic notes, pipeline stages',
  },
  {
    id: 'classroom',
    icon: '\uD83C\uDF93',
    name: 'Classroom',
    description: 'Courses, students, assignments & grades',
    features: 'Junction tables with data, date ranges, grade aggregation',
  },
  {
    id: 'project-tracker',
    icon: '\uD83D\uDCCB',
    name: 'Project Tracker',
    description: 'Asana-style tasks, projects & workflows',
    features: 'Hierarchical subtasks, multi-assignee RACI, kanban status',
  },
];

export const QuickStart: React.FC<QuickStartProps> = ({
  onLaunch,
  onBack,
  isLaunching,
  launchProgress,
}) => {
  const [selectedSchema, setSelectedSchema] = useState<QuickStartSchema | null>(null);
  const [dataSize, setDataSize] = useState<DataSize>('sample');

  const handleLaunch = () => {
    if (selectedSchema) {
      onLaunch(selectedSchema, dataSize);
    }
  };

  return (
    <div className="quickstart" role="region" aria-label="Quick Start">
      <button
        type="button"
        className="quickstart__back"
        onClick={onBack}
        disabled={isLaunching}
      >
        &larr; Back
      </button>

      <div className="quickstart__header">
        <h2 className="quickstart__title">Quick Start</h2>
        <p className="quickstart__subtitle">
          Select a schema template to explore with GraphQL.
        </p>
      </div>

      <div className="quickstart__grid">
        {SCHEMAS.map((schema) => (
          <button
            key={schema.id}
            type="button"
            className={`quickstart-card${selectedSchema === schema.id ? ' quickstart-card--selected' : ''}`}
            onClick={() => !isLaunching && setSelectedSchema(schema.id)}
            disabled={isLaunching}
            aria-pressed={selectedSchema === schema.id}
          >
            <span className="quickstart-card__icon">{schema.icon}</span>
            <span className="quickstart-card__name">{schema.name}</span>
            <span className="quickstart-card__description">{schema.description}</span>
            <span className="quickstart-card__features">{schema.features}</span>
          </button>
        ))}
      </div>

      <fieldset className="quickstart__data-size" disabled={isLaunching}>
        <legend className="quickstart__data-size-legend">Data Size</legend>
        <div className="quickstart__radio-group">
          <label className="quickstart__radio-label">
            <input
              type="radio"
              name="dataSize"
              value="sample"
              checked={dataSize === 'sample'}
              onChange={() => setDataSize('sample')}
            />
            <span className="quickstart__radio-text">
              <strong>Sample data</strong> (~50 rows/table)
            </span>
          </label>
          <label className="quickstart__radio-label">
            <input
              type="radio"
              name="dataSize"
              value="full"
              checked={dataSize === 'full'}
              onChange={() => setDataSize('full')}
            />
            <span className="quickstart__radio-text">
              <strong>Full dataset</strong> (~500-1000 rows/table)
            </span>
          </label>
        </div>
        <p className="quickstart__data-size-hint">
          Sample is great for demos. Full dataset makes filtering and pagination meaningful.
        </p>
      </fieldset>

      <div className="quickstart__launch">
        {isLaunching ? (
          <div className="quickstart__progress">
            <div className="quickstart__progress-bar">
              <div className="quickstart__progress-fill" />
            </div>
            <p className="quickstart__progress-text">{launchProgress || 'Starting...'}</p>
          </div>
        ) : (
          <button
            type="button"
            className="quickstart__launch-btn"
            onClick={handleLaunch}
            disabled={!selectedSchema}
          >
            Launch
          </button>
        )}
      </div>
    </div>
  );
};

export default QuickStart;
