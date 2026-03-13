import React from 'react';
import { PROVIDERS, ProviderSelectProps } from './types';

export const ProviderSelect: React.FC<ProviderSelectProps> = ({
  onProviderSelect,
  onBack,
}) => {
  return (
    <div className="provider-select" role="region" aria-label="Select database provider">
      <button
        type="button"
        className="provider-select__back"
        onClick={onBack}
      >
        &larr; Back
      </button>

      <div className="provider-select__header">
        <h2 className="provider-select__title">Select Database Provider</h2>
        <p className="provider-select__subtitle">
          Choose your database type to configure the connection.
        </p>
      </div>

      <div className="provider-select__grid">
        {PROVIDERS.map((provider) => (
          <button
            key={provider.id}
            type="button"
            className="provider-card"
            onClick={() => onProviderSelect(provider.id)}
          >
            <span className="provider-card__icon">{provider.icon}</span>
            <span className="provider-card__name">{provider.name}</span>
            <span className="provider-card__description">{provider.description}</span>
          </button>
        ))}
      </div>
    </div>
  );
};

export default ProviderSelect;
