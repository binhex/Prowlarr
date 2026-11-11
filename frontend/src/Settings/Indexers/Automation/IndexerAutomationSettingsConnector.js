import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { clearPendingChanges } from 'Store/Actions/baseActions';
import { fetchIndexerSchema } from 'Store/Actions/indexerActions';
import { clearIndexerAutomationSuppression, fetchIndexerAutomationState, runIndexerAutomation } from 'Store/Actions/indexerAutomationActions';
import {
  fetchIndexerAutomationSettings,
  saveIndexerAutomationSettings,
  setIndexerAutomationSettingsValue
} from 'Store/Actions/settingsActions';
import createSettingsSectionSelector from 'Store/Selectors/createSettingsSectionSelector';
import IndexerAutomationSettings from './IndexerAutomationSettings';

const SECTION = 'indexerAutomation';

function createMapStateToProps() {
  return createSelector(
    createSettingsSectionSelector(SECTION),
    (state) => state.indexerAutomation,
    (state) => state.indexers.schema.items,
    (state) => state.settings.ui.item,
    (sectionSettings, automationState, schema, uiSettings) => {
      const languages = [];

      schema.forEach((indexer) => {
        if (indexer.language && !languages.some((item) => item.key === indexer.language)) {
          languages.push({ key: indexer.language, value: indexer.language });
        }
      });

      languages.sort((a, b) => a.value.localeCompare(b.value));

      return {
        ...sectionSettings,
        stateItems: automationState.items,
        stateError: automationState.error,
        actionError: automationState.actionError,
        isValid: sectionSettings.settings.isValid ? sectionSettings.settings.isValid.value : undefined,
        dateFormat: uiSettings.shortDateFormat,
        timeFormat: uiSettings.timeFormat,
        languages
      };
    }
  );
}

const mapDispatchToProps = {
  fetchIndexerAutomationSettings,
  saveIndexerAutomationSettings,
  setIndexerAutomationSettingsValue,
  fetchIndexerAutomationState,
  clearIndexerAutomationSuppression,
  runIndexerAutomation,
  fetchIndexerSchema,
  clearPendingChanges
};

class IndexerAutomationSettingsConnector extends Component {

  //
  // Lifecycle

  componentDidMount() {
    this.props.fetchIndexerAutomationSettings();
    this.props.fetchIndexerAutomationState();
    this.props.fetchIndexerSchema();
  }

  componentWillUnmount() {
    this.props.clearPendingChanges({ section: 'settings.indexerAutomation' });
  }

  //
  // Listeners

  onInputChange = ({ name, value }) => {
    this.props.setIndexerAutomationSettingsValue({ name, value });
  };

  onSavePress = () => {
    this.props.saveIndexerAutomationSettings();
  };

  onClearSuppressionPress = (key) => {
    this.props.clearIndexerAutomationSuppression({ key });
  };

  onRunPress = () => {
    this.props.runIndexerAutomation();
  };

  //
  // Render

  render() {
    return (
      <IndexerAutomationSettings
        {...this.props}
        onInputChange={this.onInputChange}
        onSavePress={this.onSavePress}
        onRunPress={this.onRunPress}
        onClearSuppressionPress={this.onClearSuppressionPress}
      />
    );
  }
}

IndexerAutomationSettingsConnector.propTypes = {
  fetchIndexerAutomationSettings: PropTypes.func.isRequired,
  saveIndexerAutomationSettings: PropTypes.func.isRequired,
  setIndexerAutomationSettingsValue: PropTypes.func.isRequired,
  fetchIndexerAutomationState: PropTypes.func.isRequired,
  clearIndexerAutomationSuppression: PropTypes.func.isRequired,
  runIndexerAutomation: PropTypes.func.isRequired,
  fetchIndexerSchema: PropTypes.func.isRequired,
  clearPendingChanges: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(IndexerAutomationSettingsConnector);
