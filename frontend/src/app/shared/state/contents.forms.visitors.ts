/*
 * Squidex Headless CMS
 *
 * @license
 * Copyright (c) Squidex UG (haftungsbeschränkt). All rights reserved.
 */

import { ValidatorFn, Validators } from '@angular/forms';
import { DateTime, Types, ValidatorsEx } from '@app/framework';
import { ContentDto, ContentReferencesValue } from '../services/contents.service';
import { LanguageDto } from '../services/languages.service';
import { FieldDto, RootFieldDto } from '../services/schemas.service';
import { ArrayFieldPropertiesDto, AssetPreviewMode, AssetsFieldPropertiesDto, BooleanFieldPropertiesDto, ComponentFieldPropertiesDto, ComponentsFieldPropertiesDto, DateTimeFieldPropertiesDto, fieldInvariant, FieldPropertiesVisitor, GeolocationFieldPropertiesDto, JsonFieldPropertiesDto, NumberFieldPropertiesDto, ReferencesFieldPropertiesDto, StringFieldPropertiesDto, TagsFieldPropertiesDto, UIFieldPropertiesDto } from '../services/schemas.types';

export class HtmlValue {
    constructor(
        public readonly html: string,
        public readonly preview?: string,
    ) {
    }
}

export type FieldValue = string | HtmlValue;

export function getContentValue(content: ContentDto, language: LanguageDto, field: RootFieldDto, allowHtml = true): { value: any; formatted: FieldValue } {
    if (content.referenceData) {
        const reference = content.referenceData[field.name];

        const isAssets = field.properties.fieldType === 'Assets';

        if (reference && (!isAssets || allowHtml)) {
            let fieldValue: ContentReferencesValue;

            if (field.isLocalizable) {
                fieldValue = reference[language.iso2Code];
            } else {
                fieldValue = reference[fieldInvariant];
            }

            let value: string | undefined;

            if (Types.isObject(fieldValue)) {
                value = (fieldValue as any)[language.iso2Code];
            } else {
                value = fieldValue;
            }

            if (!value) {
                return { value: '-', formatted: '-' };
            }

            let formatted: FieldValue = value;

            if (isAssets && Types.isArray(value)) {
                if (value.length === 2) {
                    const buildImage = (src: string) => {
                        let format = (field.properties as any)['previewFormat'] || '';

                        if (format.indexOf('width') < 0 || format.indexOf('height') < 0) {
                            format = `width=50&height=50&mode=Pad&${format}`;
                        }

                        return `<img src="${src}?${format}" />`;
                    };

                    switch ((field.properties as any)['previewMode'] as AssetPreviewMode) {
                        case 'ImageAndFileName':
                            formatted = new HtmlValue(`<div class="image">${buildImage(value[0])} <span>${value[1]}</span></div>`, value[0]);
                            break;
                        case 'Image':
                            formatted = new HtmlValue(`<div class="image">${buildImage(value[0])}</div>`, value[0]);
                            break;
                        default:
                            formatted = value[1];
                    }
                } else if (value.length === 1) {
                    formatted = value[0];
                }
            }

            return { value, formatted };
        }
    }

    const contentField = content.data[field.name];

    if (contentField) {
        let value: any;

        if (field.isLocalizable) {
            value = contentField[language.iso2Code];
        } else {
            value = contentField[fieldInvariant];
        }

        let formatted: any;

        if (Types.isUndefined(value)) {
            formatted = value || '';
        } else {
            formatted = FieldFormatter.format(field, value, allowHtml);
        }

        return { value, formatted };
    }

    return { value: undefined, formatted: '' };
}

export class FieldFormatter implements FieldPropertiesVisitor<FieldValue> {
    private constructor(
        private readonly value: any,
        private readonly allowHtml: boolean,
    ) {
    }

    public static format(field: FieldDto, value: any, allowHtml = true) {
        if (value === null || value === undefined) {
            return '';
        }

        return field.properties.accept(new FieldFormatter(value, allowHtml));
    }

    public visitArray(_: ArrayFieldPropertiesDto): string {
        return this.formatArray('Item', 'Items');
    }

    public visitAssets(_: AssetsFieldPropertiesDto): string {
        return this.formatArray('Asset', 'Assets');
    }

    public visitComponents(_: ComponentsFieldPropertiesDto): string {
        return this.formatArray('Component', 'Components');
    }

    public visitReferences(_: ReferencesFieldPropertiesDto): string {
        return this.formatArray('Reference', 'References');
    }

    public visitBoolean(_: BooleanFieldPropertiesDto): string {
        return Types.booleanToString(this.value);
    }

    public visitComponent(_: ComponentFieldPropertiesDto): string {
        const inner = Types.objectToString(this.value, ['schemaId'], 100);

        if (inner.length > 0) {
            return `Component: ${inner}`;
        } else {
            return 'Component';
        }
    }

    public visitDateTime(properties: DateTimeFieldPropertiesDto): FieldValue {
        try {
            const parsed = DateTime.parseISO(this.value);

            if (properties.editor === 'Date') {
                return parsed.toStringFormatUTC(properties.format ?? 'P');
            } else {
                return parsed.toStringFormat(properties.format ?? 'Ppp');
            }
        } catch (ex) {
            return this.value;
        }
    }

    public visitJson(_: JsonFieldPropertiesDto): string {
        return '<Json />';
    }

    public visitUI(_: UIFieldPropertiesDto): any {
        return '';
    }

    public visitNumber(properties: NumberFieldPropertiesDto): FieldValue {
        if (!Types.isNumber(this.value)) {
            return '';
        }

        if (Types.isNumber(this.value) && properties.editor === 'Stars' && this.allowHtml) {
            if (this.value <= 0 || this.value > 6) {
                return new HtmlValue(`&#9733; ${this.value}`);
            } else {
                let html = '';

                for (let i = 0; i < this.value; i++) {
                    html += '&#9733; ';
                }

                return new HtmlValue(html);
            }
        }

        return `${this.value}`;
    }

    public visitGeolocation(_: GeolocationFieldPropertiesDto): string {
        if (!Types.isObject(this.value)) {
            return '';
        }

        return `${this.value.longitude}, ${this.value.latitude}`;
    }

    public visitTags(_: TagsFieldPropertiesDto): string {
        if (!Types.isArrayOfString(this.value)) {
            return '';
        }

        return this.value.join(', ');
    }

    public visitString(properties: StringFieldPropertiesDto): any {
        if (!Types.isString(this.value)) {
            return '';
        }

        if (properties.editor === 'StockPhoto' && this.allowHtml && this.value) {
            return new HtmlValue(`<div class="image"><img src="${thumbnail(this.value, undefined, 50)}" /></div>`, this.value);
        }

        return this.value;
    }

    private formatArray(singularName: string, pluralName: string) {
        if (!Types.isArray(this.value)) {
            return `0 ${pluralName}`;
        }

        if (this.value.length > 1) {
            return `${this.value.length} ${pluralName}`;
        } else {
            return `1 ${singularName}`;
        }
    }
}

export function thumbnail(url: string, width?: number, height?: number) {
    if (url && url.startsWith('https://images.unsplash.com')) {
        if (width) {
            return `${url}&q=80&fm=jpg&crop=entropy&cs=tinysrgb&w=${width}&fit=max`;
        }

        if (height) {
            return `${url}&q=80&fm=jpg&crop=entropy&cs=tinysrgb&h=${height}&fit=max`;
        }
    }

    return url;
}

export class FieldsValidators implements FieldPropertiesVisitor<ReadonlyArray<ValidatorFn>> {
    private constructor(
        private readonly isOptional: boolean,
    ) {
    }

    public static create(field: FieldDto, isOptional: boolean) {
        const validators = [...field.properties.accept(new FieldsValidators(isOptional))];

        if (field.properties.isRequired && !isOptional) {
            validators.push(Validators.required);
        }

        return validators;
    }

    public visitArray(properties: ArrayFieldPropertiesDto): ReadonlyArray<ValidatorFn> {
        const validators: ValidatorFn[] = [
            ValidatorsEx.betweenLength(properties.minItems, properties.maxItems),
        ];

        if (properties.uniqueFields && properties.uniqueFields.length > 0) {
            validators.push(ValidatorsEx.uniqueObjectValues(properties.uniqueFields));
        }

        return validators;
    }

    public visitAssets(properties: AssetsFieldPropertiesDto): ReadonlyArray<ValidatorFn> {
        const validators: ValidatorFn[] = [
            ValidatorsEx.betweenLength(properties.minItems, properties.maxItems),
        ];

        if (!properties.allowDuplicates) {
            validators.push(ValidatorsEx.uniqueStrings());
        }

        return validators;
    }

    public visitComponents(properties: ComponentsFieldPropertiesDto): ReadonlyArray<ValidatorFn> {
        const validators: ValidatorFn[] = [
            ValidatorsEx.betweenLength(properties.minItems, properties.maxItems),
        ];

        if (properties.uniqueFields && properties.uniqueFields.length > 0) {
            validators.push(ValidatorsEx.uniqueObjectValues(properties.uniqueFields));
        }

        return validators;
    }

    public visitNumber(properties: NumberFieldPropertiesDto): ReadonlyArray<ValidatorFn> {
        const validators: ValidatorFn[] = [
            ValidatorsEx.between(properties.minValue, properties.maxValue),
        ];

        if (properties.allowedValues && properties.allowedValues.length > 0) {
            const values: ReadonlyArray<(number | null)> = properties.allowedValues;

            if (properties.isRequired && !this.isOptional) {
                validators.push(ValidatorsEx.validValues(values));
            } else {
                validators.push(ValidatorsEx.validValues(values.concat([null])));
            }
        }

        return validators;
    }

    public visitReferences(properties: ReferencesFieldPropertiesDto): ReadonlyArray<ValidatorFn> {
        const validators: ValidatorFn[] = [
            ValidatorsEx.betweenLength(properties.minItems, properties.maxItems),
        ];

        if (!properties.allowDuplicates) {
            validators.push(ValidatorsEx.uniqueStrings());
        }

        return validators;
    }

    public visitString(properties: StringFieldPropertiesDto): ReadonlyArray<ValidatorFn> {
        const validators: ValidatorFn[] = [
            ValidatorsEx.betweenLength(properties.minLength, properties.maxLength),
        ];

        if (properties.pattern && properties.pattern.length > 0) {
            validators.push(ValidatorsEx.pattern(properties.pattern, properties.patternMessage));
        }

        if (properties.allowedValues && properties.allowedValues.length > 0) {
            const values: ReadonlyArray<string | null> = properties.allowedValues;

            if (properties.isRequired && !this.isOptional) {
                validators.push(ValidatorsEx.validValues(values));
            } else {
                validators.push(ValidatorsEx.validValues(values.concat([null])));
            }
        }

        return validators;
    }

    public visitTags(properties: TagsFieldPropertiesDto): ReadonlyArray<ValidatorFn> {
        const validators: ValidatorFn[] = [
            ValidatorsEx.betweenLength(properties.minItems, properties.maxItems),
        ];

        if (properties.allowedValues && properties.allowedValues.length > 0) {
            const values: ReadonlyArray<string | null> = properties.allowedValues;

            validators.push(ValidatorsEx.validArrayValues(values));
        }

        return validators;
    }

    public visitBoolean(_: BooleanFieldPropertiesDto): ReadonlyArray<ValidatorFn> {
        return [];
    }

    public visitComponent(_: ComponentFieldPropertiesDto): ReadonlyArray<ValidatorFn> {
        return [];
    }

    public visitDateTime(_: DateTimeFieldPropertiesDto): ReadonlyArray<ValidatorFn> {
        return [];
    }

    public visitGeolocation(_: GeolocationFieldPropertiesDto): ReadonlyArray<ValidatorFn> {
        return [];
    }

    public visitJson(_: JsonFieldPropertiesDto): ReadonlyArray<ValidatorFn> {
        return [];
    }

    public visitUI(_: UIFieldPropertiesDto): ReadonlyArray<ValidatorFn> {
        return [];
    }
}

export class FieldDefaultValue implements FieldPropertiesVisitor<any> {
    private constructor(
        private readonly partitionKey: string,
        private readonly now?: DateTime,
    ) {
    }

    public static get(field: FieldDto, partitionKey: string, now?: DateTime) {
        return field.properties.accept(new FieldDefaultValue(partitionKey, now));
    }

    public visitDateTime(properties: DateTimeFieldPropertiesDto): any {
        const now = this.now || DateTime.now();

        if (properties.calculatedDefaultValue === 'Now') {
            return `${now.toStringFormatUTC('yyyy-MM-dd\'T\'HH:mm:ss')}Z`;
        } else if (properties.calculatedDefaultValue === 'Today') {
            return `${now.toISODate()}T00:00:00Z`;
        } else {
            return this.getValue(properties.defaultValue, properties.defaultValues);
        }
    }

    public visitArray(properties: ArrayFieldPropertiesDto): any {
        if (properties.calculatedDefaultValue === 'Null') {
            return undefined;
        }

        return [];
    }

    public visitComponents(properties: ComponentsFieldPropertiesDto): any {
        if (properties.calculatedDefaultValue === 'Null') {
            return undefined;
        }

        return [];
    }

    public visitAssets(properties: AssetsFieldPropertiesDto): any {
        return this.getValue(properties.defaultValue, properties.defaultValues);
    }

    public visitBoolean(properties: BooleanFieldPropertiesDto): any {
        return this.getValue(properties.defaultValue, properties.defaultValues);
    }

    public visitComponent(_: ComponentFieldPropertiesDto): any {
        return null;
    }

    public visitGeolocation(_: GeolocationFieldPropertiesDto): any {
        return null;
    }

    public visitJson(_: JsonFieldPropertiesDto): any {
        return null;
    }

    public visitNumber(properties: NumberFieldPropertiesDto): any {
        return this.getValue(properties.defaultValue, properties.defaultValues);
    }

    public visitReferences(properties: ReferencesFieldPropertiesDto): any {
        return this.getValue(properties.defaultValue, properties.defaultValues);
    }

    public visitString(properties: StringFieldPropertiesDto): any {
        return this.getValue(properties.defaultValue, properties.defaultValues);
    }

    public visitTags(properties: TagsFieldPropertiesDto): any {
        return this.getValue(properties.defaultValue, properties.defaultValues);
    }

    public visitUI(_: UIFieldPropertiesDto): any {
        return null;
    }

    private getValue(value: any, values?: any) {
        if (values && values.hasOwnProperty(this.partitionKey)) {
            return values[this.partitionKey];
        }

        return value;
    }
}
