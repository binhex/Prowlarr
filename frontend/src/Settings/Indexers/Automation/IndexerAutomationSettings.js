import PropTypes from 'prop-types';
import React from 'react';
import Alert from 'Components/Alert';
import EnhancedSelectInput from 'Components/Form/EnhancedSelectInput';
import Form from 'Components/Form/Form';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import NewznabCategorySelectInputConnector from 'Components/Form/NewznabCategorySelectInputConnector';
import Button from 'Components/Link/Button';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import { inputTypes, kinds } from 'Helpers/Props';
import SettingsToolbarConnector from 'Settings/SettingsToolbarConnector';
import translate from 'Utilities/String/translate';
import IndexerAutomationStateTable from './IndexerAutomationStateTable';

const PROTOCOLS = [
  { key: 'torrent', value: 'torrent' },
  { key: 'usenet', value: 'usenet' }
];

const PRIVACY_LEVELS = [
  {
    key: 'private',
    get value() {
      return translate('Private');
    }
  },
  {
    key: 'semiPrivate',
    get value() {
      return translate('SemiPrivate');
    }
  },
  {
    key: 'public',
    get value() {
      return translate('Public');
    }
  }
];

function IndexerAutomationSettings(props) {
  const {
    isFetching,
    isPopulated,
    error,
    settings,
    languages,
    stateItems,
    stateError,
    actionError,
    dateFormat,
    timeFormat,
    isSaving,
    saveError,
    isValid,
    hasPendingChanges,
    onInputChange,
    onSavePress,
    onRunPress,
    onClearSuppressionPress
  } = props;

  if (isFetching && !isPopulated) {
    return (
      <PageContent title={translate('IndexerAutomation')}>
        <PageContentBody>
          <LoadingIndicator />
        </PageContentBody>
      </PageContent>
    );
  }

  return (
    <PageContent title={translate('IndexerAutomation')}>
      <SettingsToolbarConnector
        isSaving={isSaving}
        hasPendingChanges={hasPendingChanges}
        onSavePress={onSavePress}
      />

      <PageContentBody>
        {
          error &&
            <Alert kind={kinds.DANGER}>
              {translate('UnableToLoadIndexerAutomation')}
            </Alert>
        }

        {
          saveError &&
            <Alert kind={kinds.DANGER}>
              {translate('IndexerAutomationSaveFailed')}
            </Alert>
        }

        {
          stateError &&
            <Alert kind={kinds.DANGER}>
              {translate('IndexerAutomationStateFailed')}
            </Alert>
        }

        {
          isValid === false &&
            <Alert kind={kinds.WARNING}>
              {translate('IndexerAutomationInvalidSettings')}
            </Alert>
        }

        {
          actionError &&
            <Alert kind={kinds.DANGER}>
              {translate('IndexerAutomationActionFailed')}
            </Alert>
        }

        {
          isPopulated && !error &&
            <Form id="indexerAutomationSettings">
              <FormGroup>
                <FormLabel>{translate('IndexerAutomationEnabled')}</FormLabel>

                <FormInputGroup
                  type={inputTypes.CHECK}
                  name="enabled"
                  helpText={translate('IndexerAutomationEnabledHelpText')}
                  onChange={onInputChange}
                  {...settings.enabled}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>{translate('IndexerAutomationProtocols')}</FormLabel>

                <EnhancedSelectInput
                  name="protocols"
                  value={settings.protocols.value}
                  values={PROTOCOLS}
                  onChange={onInputChange}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>{translate('IndexerAutomationPrivacy')}</FormLabel>

                <EnhancedSelectInput
                  name="privacy"
                  value={settings.privacy.value}
                  values={PRIVACY_LEVELS}
                  onChange={onInputChange}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>{translate('IndexerAutomationLanguages')}</FormLabel>

                <EnhancedSelectInput
                  name="languages"
                  value={settings.languages.value}
                  values={languages}
                  onChange={onInputChange}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>{translate('IndexerAutomationCategories')}</FormLabel>

                <NewznabCategorySelectInputConnector
                  name="categories"
                  value={settings.categories.value}
                  onChange={onInputChange}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>{translate('IndexerAutomationTestTimeoutSeconds')}</FormLabel>

                <FormInputGroup
                  type={inputTypes.NUMBER}
                  name="testTimeoutSeconds"
                  unit="s"
                  helpText={translate('IndexerAutomationTestTimeoutSecondsHelpText')}
                  onChange={onInputChange}
                  {...settings.testTimeoutSeconds}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>{translate('IndexerAutomationRetryDays')}</FormLabel>

                <FormInputGroup
                  type={inputTypes.NUMBER}
                  name="retryDays"
                  unit="days"
                  helpText={translate('IndexerAutomationRetryDaysHelpText')}
                  onChange={onInputChange}
                  {...settings.retryDays}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>{translate('IndexerAutomationRemovalDays')}</FormLabel>

                <FormInputGroup
                  type={inputTypes.NUMBER}
                  name="removalDays"
                  unit="days"
                  helpText={translate('IndexerAutomationRemovalDaysHelpText')}
                  onChange={onInputChange}
                  {...settings.removalDays}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>{translate('IndexerAutomationMaxAddsPerPass')}</FormLabel>

                <FormInputGroup
                  type={inputTypes.NUMBER}
                  name="maxAddsPerPass"
                  helpText={translate('IndexerAutomationMaxAddsPerPassHelpText')}
                  onChange={onInputChange}
                  {...settings.maxAddsPerPass}
                />
              </FormGroup>
            </Form>
        }

        <IndexerAutomationStateTable
          items={stateItems}
          dateFormat={dateFormat}
          timeFormat={timeFormat}
          onClearSuppressionPress={onClearSuppressionPress}
        />

        <Button
          kind={kinds.DEFAULT}
          onPress={onRunPress}
        >
          {translate('IndexerAutomationRunNow')}
        </Button>
      </PageContentBody>
    </PageContent>
  );
}

IndexerAutomationSettings.propTypes = {
  isFetching: PropTypes.bool.isRequired,
  isPopulated: PropTypes.bool.isRequired,
  error: PropTypes.object,
  settings: PropTypes.object.isRequired,
  languages: PropTypes.arrayOf(PropTypes.object).isRequired,
  stateItems: PropTypes.arrayOf(PropTypes.object).isRequired,
  stateError: PropTypes.object,
  actionError: PropTypes.object,
  dateFormat: PropTypes.string,
  timeFormat: PropTypes.string,
  isSaving: PropTypes.bool.isRequired,
  saveError: PropTypes.object,
  isValid: PropTypes.bool,
  hasPendingChanges: PropTypes.bool.isRequired,
  onInputChange: PropTypes.func.isRequired,
  onSavePress: PropTypes.func.isRequired,
  onRunPress: PropTypes.func.isRequired,
  onClearSuppressionPress: PropTypes.func.isRequired
};

export default IndexerAutomationSettings;
