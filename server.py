"""Local stdio MCP adapter for FStudio DTO and in-process model operations. Python 3.11+."""
from __future__ import annotations

import argparse
import base64
from collections import deque
from contextlib import contextmanager
import hashlib
import json
import math
import os
from pathlib import Path
import queue
import re
import shutil
import subprocess
import sys
import threading
import time
import uuid
import xml.etree.ElementTree as ET
import zipfile
from visible_host import VisibleHost
from isolated_build_manager import IsolatedBuildManager

HERE = Path(__file__).resolve().parent
NAMESPACE = '{http://schemas.flexem.com/fs/model/1}'
SUPPORTED_PROTOCOLS = ('2025-11-25', '2025-06-18', '2025-03-26', '2024-11-05')
DEFAULT_BIN = Path(r'C:\Program Files (x86)\Flexem\FStudio 3.x\Bin')


def schema(props=None, required=()):
    return {'type': 'object', 'properties': props or {}, 'required': list(required), 'additionalProperties': False}


STR = {'type': 'string', 'minLength': 1, 'maxLength': 512}
NUM = {'type': 'number', 'minimum': 0, 'maximum': 16384}
KEY = {'type': 'string', 'minLength': 1, 'maxLength': 80}
MODEL_PATH = {'type': 'string', 'minLength': 1, 'maxLength': 512,
              'pattern': r'^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*){0,15}$'}
MODEL_PROPERTIES = {'type': 'array', 'maxItems': 60, 'items': schema(
    {'member': MODEL_PATH, 'value': {}}, ['member', 'value'])}
DRAW_CONFIG = {
    'tool': STR, 'tool_properties': {'type': 'object', 'additionalProperties': {}},
    'properties': MODEL_PROPERTIES, 'text': {'type': 'string', 'maxLength': 4096},
    'text_style': schema({'font_name':STR, 'font_size':{'type':'number','minimum':8,'maximum':120},
                          'color':{'type':'string','pattern':'^#[0-9A-Fa-f]{8}$'}, 'bold':{'type':'boolean'}}),
    'local_addresses': {'type': 'array', 'maxItems': 8, 'items': schema({
        'member': MODEL_PATH, 'address': {'type': 'integer', 'minimum': 0, 'maximum': 4294967295}},
        ['member', 'address'])},
}
DRAW_WIDGET = schema(dict(DRAW_CONFIG, preset=KEY, name=KEY, x=NUM, y=NUM,
                         width={'type': 'number', 'minimum': 1, 'maximum': 16384},
                         height={'type': 'number', 'minimum': 1, 'maximum': 16384}),
                     ['name', 'x', 'y', 'width', 'height'])
BUILD_ID = {'type': 'string', 'pattern': '^[0-9a-fA-F]{32}$'}
PAGE_UUID = {'type': 'string', 'pattern': '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'}
PAGE_NAME = {'type': 'string', 'minLength': 1, 'maxLength': 512, 'pattern': r'^(?=.*\S)[^\x00-\x1f\x7f-\x9f]+$'}
PAGE_DIMENSION = {'type': 'integer', 'minimum': 1, 'maximum': 2147483647}
TRANSMISSION_ARGS = {key: {'type': 'integer', 'minimum': 1 if key in ('count', 'cycle') else 0, 'maximum': 2147483647 if key in ('count', 'cycle') else 4294967295} for key in ('trigger_address', 'source_address', 'target_address', 'count', 'cycle')}
TRANSMISSION_REQUIRED = list(TRANSMISSION_ARGS)
TRANSMISSION_ARGS.update(data_type={'type': 'string', 'enum': ['Word', 'Bit']}, direction={'type': 'string', 'enum': ['OneWay', 'TwoWay']}, conflict_priority={'type': 'string', 'enum': ['SourceAddress', 'TargetAddress']}, direction_control_address={'type': 'integer', 'minimum': 0, 'maximum': 4294967295})
for _notice in ('before_bit', 'after_bit', 'before_word', 'after_word'):
    TRANSMISSION_ARGS[_notice] = schema({'address': {'type': 'integer', 'minimum': 0, 'maximum': 4294967295}, 'value': {'type': 'boolean'} if _notice.endswith('_bit') else {'type': 'integer', 'minimum': 0, 'maximum': 65535}}, ['address', 'value'])
PROJECT = {'project': STR}
PAGE = dict(PROJECT, page_id={'type': 'integer', 'minimum': 1, 'maximum': 28999})
WIDGET = schema({
    'kind': {'type': 'string', 'enum': ['text', 'rectangle', 'numeric_display']},
    'name': KEY, 'x': NUM, 'y': NUM,
    'width': {'type': 'number', 'minimum': 1, 'maximum': 16384},
    'height': {'type': 'number', 'minimum': 1, 'maximum': 16384},
    'text': {'type': 'string', 'maxLength': 4096},
    'font_size': {'type': 'number', 'minimum': 8, 'maximum': 120},
    'color': {'type': 'string', 'pattern': '^#[0-9A-Fa-f]{8}$'},
    'device_id': {'type': 'string', 'maxLength': 36},
    'register_id': {'type': 'integer', 'enum': [65791, 65540]},
    'address': {'type': 'integer', 'minimum': 0, 'maximum': 799999},
    'station': {'type': 'integer', 'minimum': 1, 'maximum': 247},
    'decimals': {'type': 'integer', 'minimum': 0, 'maximum': 4},
}, ['kind', 'name', 'x', 'y', 'width', 'height'])


def tool(name, description, input_schema, readonly=True):
    return {'name': name, 'description': description, 'inputSchema': input_schema,
            'annotations': {'readOnlyHint': readonly, 'destructiveHint': not readonly,
                            'idempotentHint': readonly or name == 'fstudio_upsert_widgets', 'openWorldHint': False}}


TOOLS = [
    tool('fstudio_trend_bind_sampling', 'Bind an unconfigured trend chart model on the active page or its draft to an existing serial sampling group. Requires TrendChartNormalInfo.PauseAddress to be configured first using address_configure_local. Creates enabled curve configurations with constant 0..100 scales, selects the first channel for the Y axis, and sets Immediate display. The saved two-channel sample compiles with Direction=LeftToRight; this installed compiler rejects RightToLeft. Runtime sampling is unverified. build_start compiles a saved snapshot copy in an owned private-desktop process and archives outputs separately without overwriting the editor project. Rejects missing groups and existing curve configuration instead of overwriting it. Save to persist; no runtime, settings dialog or mouse interaction.', schema({'ref':STR,'sample_id':PAGE_UUID},['ref','sample_id']),False),
    tool('fstudio_disk_bind_sampling', 'Bind an unconfigured disk curve on the active page or its draft to an existing serial sampling group. Configure NormalInfo.PauseAddress first. Creates enabled channel configurations with constant 0..100 ranges. Rejects missing groups and existing configuration; no settings dialog, mouse interaction or runtime IO.', schema({'ref':STR,'sample_id':PAGE_UUID},['ref','sample_id']),False),
    tool('fstudio_sampling_add', 'Create a project sampling group for 1..16 consecutive local UInt16 word channels with a constant cycle_ms period. Duplicate names and address overflow are rejected. History storage is disabled; all-page cyclic sampling configuration only. Returns sample_id for trend chart SamplingChannel.SampleId. Saves the native sampling configuration immediately. This project configuration has no page undo transaction; no dialog or hardware IO is executed.', schema({'name': PAGE_NAME, 'address': {'type':'integer','minimum':0,'maximum':4294967295}, 'channels': {'type':'integer','minimum':1,'maximum':16}, 'cycle_ms': {'type':'integer','minimum':1,'maximum':4294967295}}, ['name','address','channels','cycle_ms']), False),
    tool('fstudio_page_transmission_remove', 'Remove a transmission by its current model handle from the active Basic page. Rejects another page, wrong model type or already removed entry. Preserves other row identities and order, with one native undo transaction. Save to persist. No dialog or hardware operation.', schema({'ref': STR}, ['ref']), False),
    tool('fstudio_page_transmission_add', 'Add a page-level local Word or Bit transmission with constant count and a local bit OffToOn trigger. Defaults to Word/OneWay. TwoWay supports conflict_priority (SourceAddress default or TargetAddress) OR direction_control_address, never both; these options require TwoWay. Optional before_bit/after_bit notices take {address,value:boolean}; before_word/after_word take {address,value:UInt16}. Omitted notices and macros stay disabled. Requires non-overlapping source and target ranges. cycle uses 100 ms ticks, 10=1 second; stops when the page closes. One native undo transaction; save to persist. No settings dialog, graphical tool, macro or hardware operation is executed.', schema(TRANSMISSION_ARGS, TRANSMISSION_REQUIRED), False),
    tool('fstudio_health', 'Check the local FStudio DLL bridge and version. No FStudio UI or hardware connection.', schema()),
    tool('fstudio_list_models', 'List locally installed HMI model templates and their default canvas dimensions.', schema()),
    tool('fstudio_create_project', 'Create a new project from an installed model template. Refuses existing destinations. Returns initial page and local device ID.', schema({'name': KEY, 'model': KEY}, ['name', 'model']), False),
    tool('fstudio_list_pages', 'List application pages through native DLL deserialization; official system pages are excluded.', schema(PROJECT, ['project'])),
    tool('fstudio_read_page', 'Read a page and component text, bounds and addresses through FStudio DLLs.', schema(PAGE, ['project', 'page_id'])),
    tool('fstudio_add_page', 'Create a blank application page matching project dimensions, with a new ID and UUID.', schema(dict(PROJECT, name=KEY), ['project', 'name']), False),
    tool('fstudio_upsert_widgets', 'Batch-create or replace named text, rectangle and UInt16 numeric-display widgets. Stable names preserve UUID/z-order; this replaces the named widget specification. Numeric addresses default to local LW; existing Modbus devices may use register_id 65540 and 1-based addresses. Native save and reread verification, atomic commit and backup. Does not configure a PLC or compile.', schema(dict(PAGE, widgets={'type': 'array', 'items': WIDGET, 'minItems': 1, 'maxItems': 500}), ['project', 'page_id', 'widgets']), False),
    tool('fstudio_validate_project', 'Native-deserialize application pages and check duplicate IDs, widget UUIDs, bounds and device references. This is structural validation, not FStudio compilation, simulation or hardware verification.', schema(PROJECT, ['project'])),
]
TOOLS += [
    tool('fstudio_page_timer_remove','Remove a timer from the active Basic page TimerList by its current model handle. Rejects handles from another page or timers no longer present; does not identify a timer by a potentially shifted index. One native undo transaction; save to persist. No dialog or runtime action is executed.',schema({'ref':STR},['ref']),False),
    tool('fstudio_catalog', 'Discover installed native DTOs and enums by type-name query. Metadata discovery does not mean the feature has been behavior-tested.', schema({'query': {'type':'string'}, 'offset': {'type':'integer','minimum':0}, 'limit': {'type':'integer','minimum':1,'maximum':300}})),
    tool('fstudio_type_schema', 'Get native serialized property names, types, ordering and enum values. Use full type from catalog.', schema({'type':STR}, ['type'])),
    tool('fstudio_list_documents', 'List project XML/configuration files with native DTO support status. Includes system pages and configuration, excludes backups.', schema(PROJECT,['project'])),
    tool('fstudio_read_document', 'Read complete project XML with SHA-256 and native deserialization check. Relative file must come from list_documents.', schema(dict(PROJECT,file=STR),['project','file'])),
    tool('fstudio_patch_document', 'Atomically set existing XML leaf values by unambiguous path with optional 1-based sibling indexes, e.g. Graphicses/StaticTextInfo[1]/GraphicsName. Requires current SHA-256. Native save/reread and exact edited-value check; backup included. Supports existing native widgets/settings beyond convenience tools. Structural checks do not mean compile/simulation success.', schema(dict(PROJECT,file=STR,expected_sha256={'type':'string','pattern':'^[0-9a-f]{64}$'},changes={'type':'array','minItems':1,'maxItems':500,'items':schema({'path':STR,'value':{'type':'string','maxLength':65536}},['path','value'])}),['project','file','expected_sha256','changes']),False),
    tool('fstudio_capabilities', 'List installed UI command declarations and implemented MCP coverage, distinguishing metadata from callable features.', schema()),
    tool('fstudio_clone_widget', 'Clone a native widget from an existing page in the same project (including system/template pages). Preserves its configuration/references, assigns new UUID/name/component ID/z-order. Nested groups rejected. Use read_document and patch_document to edit all existing properties after cloning. Review preserved actions and addresses before runtime use.', schema(dict(PAGE,source_file=STR,widget_id={'type':'string','pattern':'^[0-9a-fA-F-]{36}$'},name=KEY),['project','page_id','source_file','widget_id','name']),False),
]
TOOLS += [
    tool('fstudio_visible_state','Read the live FStudio project and background host state. The compatibility name does not imply a visible execution-log window.',schema()),
    tool('fstudio_visible_commands','Discover native command metadata currently loaded inside FStudio. Native command dispatch is disabled with BACKGROUND_ONLY; this inventory does not expose callable background features.',schema({'query':{'type':'string'}})),
    tool('fstudio_visible_open_project','Open a workspace project through the background FStudio host. Native project upgrade/error dialogs can still occur. Returns asynchronous job ID; inspect visible_job until terminal.',schema(PROJECT,['project']),False),
    tool('fstudio_visible_run_command','Disabled compatibility endpoint. Always returns BACKGROUND_ONLY without constructing or dispatching native commands. Use dedicated background build/page/canvas/model APIs; command discovery does not imply executable command access.',schema({'command':STR},['command'])),
    tool('fstudio_visible_save','Save the active workspace project through background native services. Returns asynchronous job ID; no execution-log window is required.',schema(),False),
    tool('fstudio_visible_job','Read status/result/error of an existing background host job. Poll the same ID; do not restart after a nonterminal observation.',schema({'job_id':STR},['job_id'])),
    tool('fstudio_visible_start','Start FStudio with the local background host add-in, or reuse a verified running endpoint. The editor canvas can remain visible for drawing; no execution-log window is required. No administrator installation required.',schema(),False),
    tool('fstudio_visible_create_project','Create a new project through native HMIProjectCreator in the background host and open it in the editor. Uses the installed model template and refuses existing destinations. Returns asynchronous job ID.',schema({'name':KEY,'model':KEY},['name','model']),False),
    tool('fstudio_visible_output','Read native FStudio output categories including compiler messages.',schema()),
    tool('fstudio_visible_close_project','Close the active workspace project through background native services. While any open view is dirty the host refuses before calling native close, because the native save-changes dialog cannot be answered in background mode and the close job then never returns. Save first, or set discard_unsaved=true to skip the native window check and drop unsaved canvas changes.',schema({'discard_unsaved':{'type':'boolean'}}),False),
    tool('fstudio_model_inspect','Inspect native object properties and exact callable method signatures using a $ref from visible_state/model_get. Handles expire when the project is reopened.',schema({'ref':STR},['ref'])),
    tool('fstudio_model_get','Read a public member or dotted property path (up to 16 segments), collection item, or 100-item collection page. Prefer model_get_many for several known fields. Complex objects return $ref handles; omit member for the referenced collection.',schema({'ref':STR,'member':MODEL_PATH,'index':{'type':'integer','minimum':0},'offset':{'type':'integer','minimum':0}},['ref'])),
    tool('fstudio_model_get_many','Read 1..100 known property paths in one native UI job. Each read contains ref and member (up to 16 dot-separated segments). Returns ordered results with ref/member/value; complex values are scoped handles, collections are not expanded. Fails on an invalid path; does not recursively enumerate getters. Reuse canvas model handles instead of fetching every intermediate object.',schema({'reads':{'type':'array','minItems':1,'maxItems':100,'items':schema({'ref':STR,'member':MODEL_PATH},['ref','member'])}},['reads'])),
    tool('fstudio_model_apply_many','Apply the same known property paths/values to several scoped model refs in one transaction. Compact alternative to repeating model_set_many changes. Maximum 100 expanded changes, same page/draft required. Duplicate refs or properties are rejected. Returns one host job; save to persist.',schema({'refs':{'type':'array','minItems':1,'maxItems':100,'items':STR},'properties':dict(MODEL_PROPERTIES,minItems=1)},['refs','properties']),False),
    tool('fstudio_canvas_snapshot','Read a compact snapshot of the active page in one UI job: page identity/size, top-level control refs, UUIDs, exact Comment names, native types and actual bounds. Optional exact names filter (1..100); duplicate names return all matches and diagnostics, never silently choose one. Pagination uses offset/limit (<=100), next_offset and matched_count. page_uuid optionally guards against the wrong active page. Active-page own controls only: excludes inherited common-page composition and nested group children; not a rendered screenshot or runtime validation. Reacquire refs after reopen; use model_get_many only for additional known properties.',schema({'names':{'type':'array','minItems':1,'maxItems':100,'items':STR},'page_uuid':PAGE_UUID,'offset':{'type':'integer','minimum':0,'maximum':2147483647},'limit':{'type':'integer','minimum':1,'maximum':100}})),
    tool('fstudio_model_set','Set a permitted public native model property in the background. value is a scalar or a compatible {$ref:...}; the host rejects UI-object or interactive property paths. Prefer model_set_many for related position/size/property edits. Save the project to persist; inspect asynchronous job result.',schema({'ref':STR,'member':STR,'value':{}},['ref','member','value']),False),
    tool('fstudio_model_call','Call a permitted inspected public native model method in the background. Use exact signature for overloads, arguments as scalars or {$ref:...}. UI-object and interactive methods are blocked by the host. Returns asynchronous job ID; generic access does not make all native methods callable or verified.',schema({'ref':STR,'method':STR,'signature':{'type':'string'},'arguments':{'type':'array','items':{},'maxItems':50}},['ref','method']),False),
    tool('fstudio_model_create','Call a native model CreateAsChild/Create/CreateNew factory on a loaded Flexem.Studio type. Returns a handle via a job; add the model to the appropriate native collection/service afterward.',schema({'type':STR,'method':{'type':'string','enum':['CreateAsChild','Create','CreateNew']},'signature':{'type':'string'},'arguments':{'type':'array','items':{},'maxItems':50}},['type','method']),False),
    tool('fstudio_model_set_many','Set 1..100 properties in one background batch; member accepts a dotted path up to 16 segments from a scoped canvas/draft handle. Same page/draft required. Prevalidates paths, conversions, duplicate targets and parent/child replacement conflicts before writing. Attached pages use one undo transaction; drafts restore prior values on failure. Returns a job; poll visible_job. Save to persist.',schema({'changes':{'type':'array','minItems':1,'maxItems':100,'items':schema({'ref':STR,'member':MODEL_PATH,'value':{}},['ref','member','value'])}},['changes']),False),
]
TOOLS += [
    tool('fstudio_canvas_create_many','Create 1..30 native controls on the active Basic page in one host job: configure all drafts then insert in one native undo group. Up to 20 named presets share tool, tool_properties, properties, text_style and local_addresses; matching widget fields override. properties: <=60 per widget, <=1200 total. text and text_style are StaticTextTool-only; seed all initialized language labels using native Graphic fonts. Configure translations separately. Explicit bounds are applied last; returns requested/actual bounds. Unique names required; create-only, not upsert. Failure releases owned drafts and aborts insertion; cleanup errors reported. No save, compile, dialogs or runtime IO. Use prepare/configure/commit for unfamiliar controls.',schema({'widgets':{'type':'array','minItems':1,'maxItems':30,'items':DRAW_WIDGET},'presets':{'type':'object','additionalProperties':schema(DRAW_CONFIG)}},['widgets']),False),
    tool('fstudio_canvas_prepare','Prepare an initialized native drawing draft in the background without adding it to the canvas or opening the component property dialog. tool is the full native Flexem.Studio.GraphicsDesigner.Tools.*Tool type name; optional tool_properties sets public drawing-tool properties. Returns an asynchronous job whose result contains draft_id, model and view_model handles. Edit the initialized model with model_get/model_set, then canvas_commit or canvas_discard.',schema({'tool':STR,'tool_properties':{'type':'object','additionalProperties':{}}},['tool']),False),
    tool('fstudio_canvas_commit','Insert a prepared drawing draft at the specified canvas bounds through the native designer in the background. Only the resulting canvas update is visible; no mouse-drag animation or component-property dialog is part of this workflow. Returns an asynchronous job with the bound component and canvas state. Requires the original draft_id; this operation is not an upsert.',schema({'draft_id':STR,'name':KEY,'x':NUM,'y':NUM,'width':{'type':'number','minimum':1,'maximum':16384},'height':{'type':'number','minimum':1,'maximum':16384}},['draft_id','name','x','y','width','height']),False),
    tool('fstudio_canvas_discard','Discard an uncommitted native drawing draft in the background. Returns asynchronous job ID. Does not delete a component already committed to the canvas.',schema({'draft_id':STR},['draft_id']),False),
    tool('fstudio_canvas_graphic_remove','Remove one committed graphic from the active page canvas in the background by its model handle, inside a native undo group. The handle must be an element of the current page Graphicses collection; reopen the page after a reload. Nothing is deleted from disk until a save. Returns the removed graphic type, comment, index and remaining count.',schema({'ref':STR},['ref']),False),
    tool('fstudio_canvas_state','Read the current native drawing canvas and prepared-draft state through the background host.',schema()),
    tool('fstudio_canvas_show','Show the existing workspace canvas window without activating it, opening settings, or generating mouse input. Restores a minimized canvas so native drawing updates can be observed. Does not bring an already visible window above other applications. Returns a host job and observed visibility/focus state.',schema(),False),
    tool('fstudio_address_configure_local','Configure a direct AddressInfo or AddressData from the active canvas or its draft using the current project native local-device bit/word register factory. Preserves the address data type; changes device, register and main index in one native undo transaction on an attached page. Rejects label/index/station-reference/offset/tag-reference addresses. Returns the actual device/register/index; does not read or write hardware.',schema({'ref':STR,'address':{'type':'integer','minimum':0,'maximum':4294967295}},['ref','address']),False),
    tool('fstudio_import_image','Import a local PNG/JPG/GIF/BMP file into a StaticPictureInfo on the active canvas or its draft by setting its NormalInfo.ImageData bytes (base64 over the local pipe) and ImageImportPath. The file is validated (existence, size limit 8 MiB, image magic), copied into the project HMI/Lib/ImportImage directory and rejected when the control already imports a file; the write uses the same native undo transaction path as model_set. The native serializer drops PictureOrigin for fresh imports, so compilation keeps reporting "请从文件或者图库导入图片。"; set repair_origin=true to also save, close the project, insert <PictureOrigin>File</PictureOrigin> after the ImageImportPath element in the affected page XML, and reopen the project in one call. Save afterwards to persist. No file dialog is opened; runtime rendering is not verified.',schema({'ref':STR,'file':STR,'repair_origin':{'type':'boolean','default':False}},['ref','file']),False),
    tool('fstudio_add_language','Add a project UI language through the native LanguageConfigurationService.AddItem with a standard .NET culture name (e.g. ja-JP), reusing the font of the first existing language configuration. Rejects duplicate cultures. Save the project to persist; the language appears in LanguageConfigInfo.cfg with its LCID and survives close/reopen. No settings dialog is opened; translation workflows are not part of this call.',schema({'culture':STR},['culture']),False),
    tool('fstudio_add_user_level','Add a project user level through the native UserLevelInfos.AddUserLevelInfo factory (level 1..255) with optional password and description; duplicate levels are rejected. The native configuration is saved to HMI/System/UserLevelConfigInfo.cfg immediately and verified by count; survives close/reopen. No settings dialog is opened; control authorization and runtime login behavior are not part of this call.',schema({'level':{'type':'integer','minimum':1,'maximum':255},'password':{'type':'string','maxLength':64},'description':{'type':'string','maxLength':128}},['level']),False),
    tool('fstudio_add_plan_task','Add a plan/schedule task through the native PlanTaskInfos collection (AddNew + Add) with a description, optional enable flag and start days, then save the native plan-task configuration file immediately. Returns the task count after insertion. Duplicate descriptions are not checked; each call adds one task. No settings dialog is opened; start/end time windows and execute actions are not configured by this call.',schema({'description':STR,'enable':{'type':'boolean'},'days':{'type':'integer','minimum':0,'maximum':255},'start_address':{'type':'integer','minimum':0,'maximum':4294967295},'enable_address':{'type':'integer','minimum':0,'maximum':4294967295}},['description']),False),
    tool('fstudio_add_user_permission','Add a user account through the native UserPermissionInfos.AddNew (auto-inserted) with name, optional password, logout time and full-permission flag; duplicate names are rejected. Saves the native user-permission configuration file immediately. No settings dialog is opened; per-control authorization and runtime login behavior are not part of this call.',schema({'name':STR,'password':{'type':'string','maxLength':64},'logout_time':{'type':'integer','minimum':0,'maximum':65535},'full_permission':{'type':'boolean'},'permission_text':{'type':'string','maxLength':128}},['name']),False),
    tool('fstudio_set_home_window','Set the project home (startup) page by page UUID from pages_list, optionally also setting the init window to the same page, then save the native global configuration file immediately. No settings dialog is opened.',schema({'uuid':PAGE_UUID,'init_window':{'type':'boolean'}},['uuid']),False),
    tool('fstudio_add_plc_control','Add a PLC control command through the native PLCControlInfos.AddNew (auto-inserted) with a control_type (SwitchBasicWindow, ReportCurWindowID, BGLightControl, ProcessMacro, SoundControl, PrintScreen, ForceBuzzer, CaptureScreen), optional index and effective window UUID; duplicate control types are rejected. Saves the native PLC-control configuration file immediately. No settings dialog is opened; trigger addresses and per-type detail settings are not configured by this call.',schema({'control_type':{'type':'string','enum':['SwitchBasicWindow','ReportCurWindowID','BGLightControl','ProcessMacro','SoundControl','PrintScreen','ForceBuzzer','CaptureScreen']},'index':{'type':'integer','minimum':0,'maximum':255},'effective_window':PAGE_UUID,'trigger_address':{'type':'integer','minimum':0,'maximum':4294967295}},['control_type']),False),
    tool('fstudio_add_monitor_register','Add a monitoring register through the native MonitoringRegisters.AddNew (auto-inserted) with alias, optional id and a local word address (default 7100). Saves the native monitoring-register configuration file immediately. No settings dialog is opened.',schema({'alias':STR,'id':{'type':'integer','minimum':0,'maximum':2147483647},'address':{'type':'integer','minimum':0,'maximum':4294967295}},['alias']),False),
    tool('fstudio_set_extended_setting','Set supported extended properties through the native ExtendedPropertyInfo and save the configuration file immediately: audit_trail (bool, IsEnableAuditTrail), record_login_success (bool), record_login_failed (bool), jpeg_quality (integer 1..100). At least one setting is required. No settings dialog is opened.',schema({'audit_trail':{'type':'boolean'},'record_login_success':{'type':'boolean'},'record_login_failed':{'type':'boolean'},'jpeg_quality':{'type':'integer','minimum':1,'maximum':100}},[]),False),
    tool('fstudio_set_global_setting','Set supported project global settings through the native GlobalConfigInfo and save the configuration file immediately: show_network_icon (bool), backlit_enabled (bool), backlit_time (integer minutes), backlit_ratio (integer). At least one setting is required. No settings dialog is opened.',schema({'show_network_icon':{'type':'boolean'},'backlit_enabled':{'type':'boolean'},'backlit_time':{'type':'integer','minimum':0,'maximum':65535},'backlit_ratio':{'type':'integer','minimum':0,'maximum':100}},[]),False),
    tool('fstudio_sampling_list','List project data-sampling groups through the native DataSampleConfigInfo collection: sample_id (Guid), name and cycle in milliseconds. Read-only; no dialog or hardware IO.',schema(),False),
    tool('fstudio_sampling_remove','Remove a sampling group by sample_id from the native DataSampleConfigInfo collection and save the configuration file immediately. Sampling groups referenced by trend/disk curves become dangling; remove those controls first. No dialog or hardware IO.',schema({'sample_id':PAGE_UUID},['sample_id']),False),
    tool('fstudio_sampling_update','Update a sampling group name and/or cycle (ms) by sample_id through the native PropertyInfo and save the configuration file immediately. Addresses and channels are not changed by this call; no dialog or hardware IO.',schema({'sample_id':PAGE_UUID,'name':PAGE_NAME,'cycle_ms':{'type':'integer','minimum':1,'maximum':4294967295}},['sample_id']),False),
]
TOOLS += [
    tool('fstudio_build_start','Compile the saved active workspace project snapshot in an owned private-desktop process. Save first. Returns build_id directly; poll build_status. Canvas editing can continue; source_unchanged reports saved-file changes. Outputs are archived separately, not installed into the editor project. No desktop switching.',schema(),False),
    tool('fstudio_build_status','Read a persistent isolated build by build_id. Includes compiler_success, native_result diagnostics, outputs directory and hashes, source_unchanged and cleanup_complete. Terminal states: completed, failed, cancelled, ui_required, cleanup_failed. completed alone does not prove compiler_success.',schema({'build_id':BUILD_ID},['build_id'])),
    tool('fstudio_build_diagnostics','Read the current native error-report service and existing build output without creating or opening an output/error window. This is a snapshot of shared native state; entries are not automatically attributed to a particular build.',schema()),
    tool('fstudio_canvas_configure_bit_switch','Initialize a SwitchInfo with an empty action list using a native KeyDown bit action. action is On, Off or Switch (toggle); address is an explicit local-device bit address. The native factory selects the local device and its first bit register; the response identifies both. Existing actions are preserved by rejecting nonempty lists. Works on a draft or a widget on the active canvas without opening settings; save to persist.',schema({'ref':STR,'action':{'type':'string','enum':['On','Off','Switch']},'address':{'type':'integer','minimum':0,'maximum':4294967295}},['ref','action','address']),False),
    tool('fstudio_build_cancel','Request cancellation of an isolated build. Keep polling the same build_id until terminal and cleanup_complete. A completion racing with cancellation retains the actual native result. Finished tasks return their existing state.',schema({'build_id':BUILD_ID},['build_id']),False),
    tool('fstudio_pages_list','List native pages of the active workspace project, including all page kinds, UUIDs, dimensions, file paths, opened and dirty states. The background page mutation/open APIs support only non-reserved Basic business pages.',schema()),
    tool('fstudio_page_create','Create and save a native Basic business page in the active project without opening settings or automatically opening the new canvas. name is required; kind may only be Basic. Optional integer width/height default to native dimensions and must satisfy the installed model bounds. Returns an asynchronous job with page metadata.',schema({'name':PAGE_NAME,'kind':{'type':'string','enum':['Basic'],'default':'Basic'},'width':PAGE_DIMENSION,'height':PAGE_DIMENSION},['name']),False),
    tool('fstudio_page_open','Open or activate a non-reserved Basic business page by UUID from pages_list. Uses the native page service with show_settings=false; no property window or mouse-drag input. Returns an asynchronous job with the opened page.',schema({'uuid':PAGE_UUID},['uuid']),False),
    tool('fstudio_page_timer_add','Add a native timer task to the active Basic page TimerList, not a canvas TimerTool graphic. Uses a local bit OffToOn trigger and SetON/SetOFF/CycleSwitch action; stops when the page closes. cycle uses 100 ms ticks with high-speed timing disabled: 10=1 second (also returned as native_cycle_description). Returns timer handle, actual addresses and one native undo transaction. Save to persist. Does not open page settings or execute runtime actions.',schema({'trigger_address':{'type':'integer','minimum':0,'maximum':4294967295},'target_address':{'type':'integer','minimum':0,'maximum':4294967295},'cycle':{'type':'integer','minimum':1,'maximum':2147483647},'action':{'type':'string','enum':['SetON','SetOFF','CycleSwitch']}},['trigger_address','target_address','cycle','action']),False),
    tool('fstudio_page_rename','Rename a non-reserved Basic page by UUID in the background. Open pages use a native undo transaction and require visible_save to persist; closed pages are backed up and saved immediately. Returns an asynchronous job reporting saved/persistence and backup when applicable.',schema({'uuid':PAGE_UUID,'name':PAGE_NAME},['uuid','name']),False),
    tool('fstudio_page_copy','Copy a non-reserved Basic page within the active project, using its current designer model if open. Deep-copies native DTO data and refreshes page and nested graphics UUIDs; external resource, address and page references are preserved. The saved copy is registered without automatically opening it. Returns an asynchronous job with page metadata and refreshed_identities.',schema({'uuid':PAGE_UUID,'name':PAGE_NAME},['uuid','name']),False),
    tool('fstudio_page_delete','Delete a non-reserved Basic page by UUID through the background native API without a confirmation dialog. Backs up its file and, when open, current model; protects the last Basic page, startup/home, active screensaver and special pages. Returns an asynchronous job with confirmed deletion and backup path.',schema({'uuid':PAGE_UUID},['uuid']),False),
]
BY_NAME = {t['name']: t for t in TOOLS}


def validate(value, spec, at='arguments'):
    """按工具 schema 校验参数；不匹配时抛出带字段路径的 ValueError。

    整数保持任意精度，仅浮点数检查有限性，避免非法大整数触发内部溢出。
    此函数仅校验输入，不分发原生操作。
    """
    typ = spec.get('type')
    good = {'object': isinstance(value, dict), 'array': isinstance(value, list),
            'string': isinstance(value, str), 'integer': type(value) is int,
            'number': type(value) is int or (type(value) is float and math.isfinite(value)),
            'boolean': type(value) is bool}
    if typ and not good[typ]:
        raise ValueError(f'{at}: expected {typ}')
    if 'enum' in spec and value not in spec['enum']:
        raise ValueError(f'{at}: value is not supported')
    if isinstance(value, dict):
        for key in spec.get('required', []):
            if key not in value: raise ValueError(f'{at}.{key}: required')
        properties = spec.get('properties', {})
        additional = spec.get('additionalProperties', True)
        extra = set(value) - set(properties)
        if extra and additional is False:
            raise ValueError(f'{at}: unknown fields {sorted(extra)}')
        for key, item in value.items():
            if key in properties:
                validate(item, properties[key], at + '.' + key)
            elif isinstance(additional, dict):
                validate(item, additional, at + '.' + key)
    if typ == 'array':
        if not spec.get('minItems', 0) <= len(value) <= spec.get('maxItems', 1000000): raise ValueError(f'{at}: invalid array length')
        for i, item in enumerate(value): validate(item, spec['items'], f'{at}[{i}]')
    if typ == 'string':
        if not spec.get('minLength', 0) <= len(value) <= spec.get('maxLength', 1000000): raise ValueError(f'{at}: invalid string length')
        if 'pattern' in spec and not re.fullmatch(spec['pattern'], value): raise ValueError(f'{at}: invalid format')
    if typ in ('number', 'integer'):
        if not spec.get('minimum', -math.inf) <= value <= spec.get('maximum', math.inf): raise ValueError(f'{at}: outside allowed range')


class NativeBridge:
    def __init__(self, bin_dir):
        exe = HERE / 'artifacts/FStudioBridge.exe'
        for path in (exe, bin_dir / 'Flexem.Studio.Core.dll', bin_dir / 'Flexem.Studio.Dtos.dll'):
            if not path.is_file(): raise RuntimeError(f'Missing runtime file: {path}')
        self.proc = subprocess.Popen([str(exe), str(bin_dir)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                     stderr=subprocess.PIPE, text=True, encoding='utf-8', bufsize=1,
                                     creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        self.responses = queue.Queue()
        self.stderr = deque(maxlen=20)
        def reader():
            for line in self.proc.stdout: self.responses.put(line)
            self.responses.put(None)
        threading.Thread(target=reader, daemon=True).start()
        threading.Thread(target=lambda: self.stderr.extend(self.proc.stderr), daemon=True).start()

    def call(self, op, **args):
        if self.proc.poll() is not None: raise RuntimeError('Native bridge exited: ' + ''.join(self.stderr)[-2000:])
        self.proc.stdin.write(json.dumps({'op': op, 'args': args}, ensure_ascii=False, allow_nan=False) + '\n')
        self.proc.stdin.flush()
        try: line = self.responses.get(timeout=30)
        except queue.Empty:
            self.proc.kill(); raise RuntimeError('Native bridge timed out; destination was not committed')
        if line is None: raise RuntimeError('Native bridge closed: ' + ''.join(self.stderr)[-2000:])
        result = json.loads(line)
        if not result.get('ok'): raise RuntimeError(result.get('error', 'Native bridge failed'))
        return result['data']

    def close(self):
        if self.proc.poll() is None:
            self.proc.stdin.close()
            try: self.proc.wait(timeout=3)
            except subprocess.TimeoutExpired: self.proc.kill(); self.proc.wait(timeout=3)
        for stream in (self.proc.stdout, self.proc.stderr): stream.close()


class FStudio:
    def __init__(self, workspace, bin_dir):
        self.workspace = workspace.resolve()
        self.workspace.mkdir(parents=True, exist_ok=True)
        self.bin = bin_dir.resolve()
        self.templates = self.bin.parent / 'Template/HMI/zh'
        self.native = NativeBridge(self.bin)
        self.visible = VisibleHost(HERE/'artifacts/visible-host/endpoint.json')
        self.builds = IsolatedBuildManager(HERE,self.workspace,self.bin)

    def inside(self, path):
        path = Path(path)
        if not path.is_absolute(): path = self.workspace / path
        path = path.resolve()
        if not path.is_relative_to(self.workspace) or path == self.workspace:
            raise ValueError('Project path must be inside the configured workspace')
        return path

    def project(self, value):
        path = self.inside(value)
        if path.suffix == '.fsprj': path = path.parent
        if not path.is_dir() or len(list(path.glob('*.fsprj'))) != 1:
            raise ValueError('Expected one .fsprj file in an existing project directory')
        self.inside(path / 'HMI/System/ConnectInfo.cfg')
        return path

    @staticmethod
    def model_name(name):
        if not re.fullmatch(r'F[0-9][A-Za-z0-9_-]{1,40}', name): raise ValueError('Invalid HMI model name')
        return name

    @staticmethod
    def file_name(name):
        if any(x in name for x in '<>:"/\\|?*') or name.endswith((' ', '.')) or name in ('.', '..') or any(ord(c) < 32 for c in name):
            raise ValueError('Invalid project name')
        if re.fullmatch(r'(?i)(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?', name): raise ValueError('Reserved project name')
        return name

    def _template(self, model):
        path = self.templates / (self.model_name(model) + '.tpl')
        if not path.is_file(): raise ValueError('Installed model template not found')
        return path

    def list_models(self):
        models = []
        for tpl in sorted(self.templates.glob('F[0-9]*.tpl')):
            try:
                with zipfile.ZipFile(tpl, metadata_encoding='gbk') as z:
                    for n in z.namelist():
                        if n.startswith('HMI/Frame/Basic/') and n.endswith('.wcfg'):
                            r = ET.fromstring(z.read(n))
                            if r.findtext(NAMESPACE+'Id') == '1':
                                models.append({'model': tpl.stem, 'width': int(r.findtext(NAMESPACE+'Width')), 'height': int(r.findtext(NAMESPACE+'Height'))}); break
            except (zipfile.BadZipFile, ET.ParseError, UnicodeError, ValueError): continue
        return {'models': models}

    def _device_info(self, project):
        r = ET.parse(self.inside(project / 'HMI/System/ConnectInfo.cfg')).getroot()
        devices = {e.text for e in r.iter(NAMESPACE+'DeviceId') if e.text}
        return r.findtext(NAMESPACE+'DeviceId'), devices, r.findtext(NAMESPACE+'Name')

    def page_files(self, project):
        result = []
        for file in sorted(self.inside(project / 'HMI/Frame/Basic').glob('*.wcfg')):
            file = self.inside(file)
            r = ET.parse(file).getroot()
            if 0 < int(r.findtext(NAMESPACE+'Id', '0')) < 29000: result.append(file)
        return result

    def find_page(self, project, page_id):
        matches = [p for p in self.page_files(project) if int(ET.parse(p).getroot().findtext(NAMESPACE+'Id')) == page_id]
        if len(matches) != 1: raise ValueError(f'Expected one page for ID {page_id}; found {len(matches)}')
        return matches[0]

    @contextmanager
    def lock(self, project):
        marker = self.inside(project / '.fstudio-mcp.lock')
        try: fd = os.open(marker, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
        except FileExistsError: raise RuntimeError('Another MCP edit owns this project lock')
        try:
            with os.fdopen(fd, 'w') as f: f.write(str(os.getpid()))
            yield
        finally: marker.unlink(missing_ok=True)

    def create_project(self, name, model):
        dest = self.inside(self.file_name(name)); template = self._template(model)
        if dest.exists(): raise ValueError('Project destination already exists; choose another name')
        stage = self.inside('.create-' + uuid.uuid4().hex)
        stage.mkdir()
        try:
            with zipfile.ZipFile(template, metadata_encoding='gbk') as z:
                if sum(i.file_size for i in z.infolist()) > 256 * 1024 * 1024: raise ValueError('Template exceeds size limit')
                for info in z.infolist():
                    target = (stage / info.filename).resolve()
                    if not target.is_relative_to(stage): raise ValueError('Unsafe archive path')
                    if info.is_dir(): target.mkdir(parents=True, exist_ok=True)
                    else:
                        target.parent.mkdir(parents=True, exist_ok=True)
                        with z.open(info) as source, target.open('xb') as output: shutil.copyfileobj(source, output)
            entries = list(stage.glob('*.fsprj'))
            if len(entries) != 1: raise ValueError('Model template contains an ambiguous project entry')
            entries[0].rename(stage / (name + '.fsprj'))
            first = self.find_page(stage, 1)
            summary = self.native.call('read_page', path=str(first))
            local, _, actual_model = self._device_info(stage)
            if actual_model != model: raise ValueError('Model template identity mismatch')
            stage.rename(dest)
            return {'project': str(dest), 'entry': str(dest / (name+'.fsprj')), 'model': model,
                    'local_device_id': local, 'initial_page': summary, 'validation': 'native_deserialization_only'}
        finally:
            if stage.exists():
                assert stage.resolve().is_relative_to(self.workspace)
                shutil.rmtree(stage)

    def read_page(self, project, page_id):
        folder = self.project(project); path = self.find_page(folder, page_id)
        result = self.native.call('read_page', path=str(path)); result['file'] = str(path); return result

    def list_pages(self, project):
        folder = self.project(project); pages = []
        for file in self.page_files(folder):
            p = self.native.call('read_page', path=str(file)); p.pop('widgets'); p['file'] = str(file); pages.append(p)
        return {'pages': pages, 'scope': 'application_pages'}

    def add_page(self, project, name):
        folder = self.project(project)
        with self.lock(folder):
            existing = self.list_pages(str(folder))['pages']
            if any(p['name'] == name for p in existing): raise ValueError('Page name already exists')
            new_id = max(p['id'] for p in existing) + 1
            if new_id >= 29000: raise ValueError('Application page ID range exhausted')
            source = Path(existing[0]['file']); new_uuid = str(uuid.uuid4())
            dest = self.inside(source.parent / (new_uuid+'.wcfg')); temp = dest.with_suffix('.mcp-staging')
            try:
                result = self.native.call('apply_page', input=str(source), output=str(temp), page_name=name, new_page_id=new_id, new_page_uuid=new_uuid)
                temp.rename(dest); result['file'] = str(dest); return result
            finally: temp.unlink(missing_ok=True)

    def _widgets(self, specs, page, project):
        local_id, devices, _ = self._device_info(project)
        names = set()
        for spec in specs:
            if spec['name'] in names: raise ValueError('Duplicate widget name in batch')
            names.add(spec['name'])
            if spec['x'] + spec['width'] > page['width'] or spec['y'] + spec['height'] > page['height']:
                raise ValueError(f"Widget {spec['name']} exceeds page bounds")
            if spec['kind'] == 'text' and 'text' not in spec: raise ValueError('Text widget requires text')
            if spec['kind'] == 'numeric_display':
                if 'address' not in spec: raise ValueError('Numeric widget requires address')
                spec.setdefault('device_id', local_id); spec.setdefault('register_id', 65791)
                if spec['device_id'] not in devices: raise ValueError('Unknown device ID; configure the device in the project first')
                if spec['register_id'] == 65791 and spec['device_id'] != local_id: raise ValueError('LW register requires the local device')
                if spec['register_id'] == 65540 and (spec['device_id'] == local_id or not 1 <= spec['address'] <= 65536):
                    raise ValueError('Modbus holding register requires an existing PLC device and a 1-based address in 1..65536')
        return specs

    def upsert_widgets(self, project, page_id, widgets):
        folder = self.project(project)
        with self.lock(folder):
            path = self.find_page(folder, page_id); before = path.read_bytes()
            page = self.native.call('read_page', path=str(path))
            specs = self._widgets(widgets, page, folder)
            temp = self.inside(path.with_suffix('.'+uuid.uuid4().hex+'.mcp-staging'))
            try:
                start = time.perf_counter()
                result = self.native.call('apply_page', input=str(path), output=str(temp), widgets=specs)
                if path.read_bytes() != before: raise RuntimeError('Page changed outside MCP; destination was not overwritten')
                backup_dir = self.inside(folder / '.mcp-backups'); backup_dir.mkdir(exist_ok=True)
                backup = backup_dir / (path.stem+'.'+uuid.uuid4().hex+'.wcfg'); backup.write_bytes(before)
                os.replace(temp, path)
                result.update(file=str(path), backup=str(backup), elapsed_ms=round((time.perf_counter()-start)*1000, 2), native_roundtrip_verified=True)
                return result
            finally: temp.unlink(missing_ok=True)

    def validate_project(self, project):
        folder = self.project(project); problems = []; seen_pages = set(); seen_uuid = set(); native_reads = 0
        _, devices, model = self._device_info(folder)
        for file in self.page_files(folder):
            try: page = self.native.call('read_page', path=str(file)); native_reads += 1
            except Exception as e:
                problems.append({'file': str(file), 'error': str(e)}); continue
            if page['id'] in seen_pages: problems.append({'file': str(file), 'error': 'Duplicate page ID'})
            seen_pages.add(page['id'])
            for item in [page] + page['widgets']:
                key = item.get('uuid', item.get('id'))
                if key in seen_uuid: problems.append({'file': str(file), 'error': 'Duplicate page/widget UUID', 'uuid': key})
                seen_uuid.add(key)
            for w in page['widgets']:
                b = w.get('bounds')
                if b and all(type(b.get(k)) in (int, float) for k in ('x','y','width','height')):
                    if b['x'] < 0 or b['y'] < 0 or b['width'] <= 0 or b['height'] <= 0 or b['x']+b['width'] > page['width'] or b['y']+b['height'] > page['height']:
                        problems.append({'file': str(file), 'widget': w['name'], 'error': 'Out-of-bounds component'})
                a = w.get('address')
                if a and a['device_id'] not in devices: problems.append({'file': str(file), 'widget': w['name'], 'error': 'Unknown device reference'})
        return {'valid': not problems, 'model': model, 'native_pages_read': native_reads, 'scope': 'application_pages',
                'diagnostics': problems, 'compiler': 'not_run', 'simulation': 'not_run', 'hardware': 'not_run'}

    def catalog(self, **args):
        return self.native.call('catalog', **args)

    def visible_state(self):
        return self.visible.read('state')

    def visible_start(self):
        return self.visible.start(self.bin)

    @staticmethod
    def check_native_project_directory(directory):
        """按原生 UTF-16 长度限制拒绝超长目录，避免不可交互的系统提示阻塞宿主。"""
        length = len(str(directory).encode('utf-16-le')) // 2
        if length > 90:
            raise ValueError(f'FStudio project directory exceeds native 90-character limit ({length}); use a shorter workspace or project name')

    def visible_create_project(self, name, model):
        """校验目标后提交原生创建任务；路径超限时不写文件、不调用宿主。"""
        self.file_name(name)
        dest = self.inside(name)
        self.check_native_project_directory(dest)
        if dest.exists(): raise ValueError('Destination already exists')
        with zipfile.ZipFile(self._template(model),metadata_encoding='gbk') as template:
            xml = template.read('HMI/System/ConnectInfo.cfg').decode('utf-8-sig')
        return self.visible.request('create_project',name=name,model=model,template_xml=xml)

    def visible_output(self):
        return self.visible.read('output')

    def visible_close_project(self, discard_unsaved=False):
        return self.visible.request('close_project', discard_unsaved=bool(discard_unsaved))

    def model_inspect(self, **args):
        return self.visible.read('inspect_object', **args)

    def model_get(self, **args):
        """读取已知原生属性路径或集合页，保持原有单项结果格式。"""
        return self.visible.read('read_member', **args)

    def model_get_many(self, reads):
        """在一个宿主任务中读取多个路径，不逐层产生客户端往返。"""
        return self.visible.read('read_members', reads=reads)

    def model_apply_many(self, refs, properties):
        """将共用属性展开成一个受限批次；重复或超限时不触达原生宿主。"""
        if len(refs)!=len(set(refs)):raise ValueError('Duplicate refs')
        members=[p['member'] for p in properties]
        if len(members)!=len(set(members)):raise ValueError('Duplicate properties')
        if not refs or not properties or len(refs)*len(properties)>100:
            raise ValueError('Expected 1..100 expanded property changes')
        return self.model_set_many([dict(p,ref=ref) for ref in refs for p in properties])

    def model_set(self, **args):
        return self.visible.request('set_member', **args)

    def model_set_many(self, changes):
        """提交深层属性批次，由宿主预校验并执行同一范围的撤销事务。"""
        return self.visible.request('set_members', changes=changes)

    def model_call(self, **args):
        return self.visible.request('invoke_method', **args)

    def model_create(self, **args):
        return self.visible.request('native_factory', **args)

    def canvas_prepare(self, **args):
        return self.visible.request('drawing_prepare', **args)

    def canvas_snapshot(self, **args):
        """一次读取当前页的稳定名称、身份和边界；按需分页，句柄仍仅属于当前会话。"""
        names=args.get('names')
        if names is not None and len(names)!=len(set(names)):
            raise ValueError('Duplicate requested names')
        return self.visible.read('drawing_snapshot',**args)

    def canvas_create_many(self, widgets, presets=None):
        """合并共用预设和控件覆盖值，一次提交声明；宿主负责回滚并报告清理失败。

        同路径的控件专属配置覆盖预设；每层自身重复路径、未知预设和缺少工具均拒绝。
        原生模型路径、类型、引用和插入是否成功仍由宿主验证。
        """
        presets=presets or {}
        if len(presets)>20:raise ValueError('At most 20 presets are allowed')
        resolved=[];names=set();property_count=0
        for widget in widgets:
            name=widget['name']
            if name in names:raise ValueError('Duplicate widget name: '+name)
            names.add(name)
            preset=widget.get('preset')
            if preset is not None and preset not in presets:raise ValueError('Unknown preset: '+preset)
            base=presets.get(preset,{})
            item=dict(base,**{k:v for k,v in widget.items() if k!='preset'})
            if not item.get('tool'):raise ValueError('Widget requires tool or a preset with tool: '+name)
            item['tool_properties']=dict(base.get('tool_properties',{}),**widget.get('tool_properties',{}))
            if 'text_style' in base or 'text_style' in widget:
                item['text_style']=dict(base.get('text_style',{}),**widget.get('text_style',{}))
            for field in ('properties','local_addresses'):
                merged={}
                for layer in (base.get(field,[]),widget.get(field,[])):
                    paths=[entry['member'] for entry in layer]
                    if len(paths)!=len(set(paths)):raise ValueError('Duplicate '+field+' path in '+name)
                    merged.update((entry['member'],dict(entry)) for entry in layer)
                item[field]=list(merged.values())
                if len(item[field])>(60 if field=='properties' else 8):raise ValueError('Too many '+field+' in '+name)
            property_count+=len(item['properties'])
            # 身份与布局由批量插入统一管理，不能被属性预设改成共享身份或旧位置。
            reserved={'Comment','UniqueId','ComponentId','Position','IsReferenced'}
            if any(p['member'].split('.')[0] in reserved for p in item['properties']):
                raise ValueError('Use explicit name/bounds; identity and lifecycle properties are not preset fields')
            resolved.append(item)
        if not 1<=len(resolved)<=30 or property_count>1200:
            raise ValueError('Expected 1..30 widgets and at most 1200 configured properties')
        return self.visible.request('drawing_create_many',widgets=resolved)

    def canvas_commit(self, **args):
        return self.visible.request('drawing_commit', **args)

    def canvas_discard(self, draft_id):
        return self.visible.request('drawing_discard', draft_id=draft_id)

    def canvas_graphic_remove(self, ref):
        return self.visible.request('drawing_remove_graphic', ref=ref)

    def canvas_state(self):
        return self.visible.read('drawing_state')

    def canvas_show(self):
        return self.visible.request('drawing_show_canvas')

    def address_configure_local(self, **args):
        return self.visible.request('drawing_configure_local_address', **args)

    def add_language(self, culture):
        culture = (culture or '').strip()
        if not culture or len(culture) > 64:
            raise ValueError('culture must be a non-empty .NET culture name, e.g. ja-JP')
        return self.visible.request('page_add_language', culture=culture)

    def add_user_level(self, level, password=None, description=None):
        args = {'level': level}
        if password is not None: args['password'] = password
        if description is not None: args['description'] = description
        return self.visible.request('page_add_user_level', **args)

    def add_plan_task(self, description, enable=None, days=None, start_address=None, enable_address=None):
        args = {'description': description}
        if enable is not None: args['enable'] = enable
        if days is not None: args['days'] = days
        if start_address is not None: args['start_address'] = start_address
        if enable_address is not None: args['enable_address'] = enable_address
        return self.visible.request('page_add_plan_task', **args)

    def add_user_permission(self, name, password=None, logout_time=None, full_permission=None, permission_text=None):
        args = {'name': name}
        if password is not None: args['password'] = password
        if logout_time is not None: args['logout_time'] = logout_time
        if full_permission is not None: args['full_permission'] = full_permission
        if permission_text is not None: args['permission_text'] = permission_text
        return self.visible.request('page_add_user_permission', **args)

    def set_home_window(self, uuid, init_window=None):
        args = {'uuid': uuid}
        if init_window is not None: args['init_window'] = init_window
        return self.visible.request('page_set_home_window', **args)

    def add_plc_control(self, control_type, index=None, effective_window=None, trigger_address=None):
        args = {'control_type': control_type}
        if index is not None: args['index'] = index
        if effective_window is not None: args['effective_window'] = effective_window
        if trigger_address is not None: args['trigger_address'] = trigger_address
        return self.visible.request('page_add_plc_control', **args)

    def add_monitor_register(self, alias, id=None, address=None):
        args = {'alias': alias}
        if id is not None: args['id'] = id
        if address is not None: args['address'] = address
        return self.visible.request('page_add_monitor_register', **args)

    def set_extended_setting(self, audit_trail=None, record_login_success=None, record_login_failed=None, jpeg_quality=None):
        args = {}
        if audit_trail is not None: args['audit_trail'] = audit_trail
        if record_login_success is not None: args['record_login_success'] = record_login_success
        if record_login_failed is not None: args['record_login_failed'] = record_login_failed
        if jpeg_quality is not None: args['jpeg_quality'] = jpeg_quality
        if not args: raise ValueError('at least one setting is required')
        return self.visible.request('page_set_extended_setting', **args)

    def set_global_setting(self, show_network_icon=None, backlit_enabled=None, backlit_time=None, backlit_ratio=None):
        args = {}
        if show_network_icon is not None: args['show_network_icon'] = show_network_icon
        if backlit_enabled is not None: args['backlit_enabled'] = backlit_enabled
        if backlit_time is not None: args['backlit_time'] = backlit_time
        if backlit_ratio is not None: args['backlit_ratio'] = backlit_ratio
        if not args: raise ValueError('at least one setting is required')
        return self.visible.request('page_set_global_setting', **args)

    def sampling_list(self):
        return self.visible.request('page_sampling_list')

    def sampling_remove(self, sample_id):
        return self.visible.request('page_sampling_remove', sample_id=sample_id)

    def sampling_update(self, sample_id, name=None, cycle_ms=None):
        args = {'sample_id': sample_id}
        if name is not None: args['name'] = name
        if cycle_ms is not None: args['cycle_ms'] = cycle_ms
        return self.visible.request('page_sampling_update', **args)

    def import_image(self, ref, file, repair_origin=False):
        """向空图片控件导入本地文件，校验失败或同名内容冲突时保留工程资源。

        ref 为当前画布或草稿句柄；file 为支持的图片路径。repair_origin 为真时
        保存、重开并修补原生来源字段，返回导入结果；无效参数通过异常拒绝。
        """
        path = Path(file).resolve()
        if not path.is_file():
            raise ValueError(f'image file not found: {path}')
        if path.stat().st_size > 8 * 1024 * 1024:
            raise ValueError('image exceeds the 8 MiB import limit')
        data = path.read_bytes()
        if not (data.startswith(b'\x89PNG\r\n\x1a\n') or data.startswith(b'\xff\xd8\xff')
                or data.startswith(b'GIF87a') or data.startswith(b'GIF89a') or data.startswith(b'BM')):
            raise ValueError('unsupported image format; expected PNG, JPG, GIF or BMP')
        state = self.visible.read('state')
        project_file = state.get('project') if isinstance(state, dict) else None
        if not project_file:
            raise RuntimeError('No active project; open the target project before importing an image')
        project_dir = Path(project_file).resolve().parent
        import_dir = project_dir / 'HMI' / 'Lib' / 'ImportImage'
        target = import_dir / path.name
        normal = self.visible.read('read_member', ref=ref, member='NormalInfo')
        normal_ref = normal.get('value', {}).get('$ref') if isinstance(normal, dict) else None
        if not normal_ref:
            raise ValueError('ref does not expose a NormalInfo object; expected a StaticPictureInfo handle')
        existing = self.visible.read('read_member', ref=normal_ref, member='ImageImportPath')
        existing_path = existing.get('value') if isinstance(existing, dict) else None
        if existing_path:
            raise ValueError('this picture already imports a file; import into an empty StaticPicture control')
        # 先校验控件，再创建文件；已有同名资源只能复用相同内容，不能覆盖其他控件的图片。
        if target.resolve() != path:
            if target.exists():
                if target.read_bytes() != data:
                    raise ValueError('destination image already exists with different content; use a different file name')
            else:
                import_dir.mkdir(parents=True, exist_ok=True)
                # 排他创建防止校验之后其他请求写入同名文件。
                with target.open('xb') as stream:
                    stream.write(data)
        job = self.visible.request('set_members', changes=[
            {'ref': normal_ref, 'member': 'ImageData', 'value': base64.b64encode(data).decode('ascii')},
            {'ref': normal_ref, 'member': 'ImageImportPath', 'value': str(target)},
        ])
        result = {'imported_file': str(target), 'bytes': len(data), 'job': job, 'repair_origin': None}
        if not repair_origin:
            result['note'] = ('The native serializer drops PictureOrigin when saving a fresh import; compilation '
                              'will report "请从文件或者图库导入图片。" until the page XML carries '
                              '<PictureOrigin>File</PictureOrigin> after the ImageImportPath element. For automatic '
                              'repair, pass repair_origin=true on the initial import; populated controls reject reimport.')
            return result
        # 自动修补：保存 -> 关闭 -> 修补页面 XML -> 重开
        save_job = self.visible.request('save_project')
        self._wait_job(save_job)
        close_job = self.visible.request('close_project')
        self._wait_job(close_job)
        patched = []
        for wcfg in (project_dir / 'HMI' / 'Frame').rglob('*.wcfg'):
            text = wcfg.read_text(encoding='utf-8-sig')
            if str(target).replace('/', '\\') not in text.replace('/', '\\'):
                continue
            changed = 0
            out, cursor = [], 0
            for match in re.finditer(r'(<ImageImportPath>[^<]*</ImageImportPath>)(?!\s*<PictureOrigin>)', text):
                out.append(text[cursor:match.end()])
                out.append('<PictureOrigin>File</PictureOrigin>')
                cursor = match.end(); changed += 1
            if not changed:
                continue
            out.append(text[cursor:])
            wcfg.write_text(''.join(out), encoding='utf-8-sig')
            patched.append({'file': str(wcfg), 'inserted': changed})
        open_job = self.visible.request('open_project', file=str(Path(project_file).resolve()))
        self._wait_job(open_job, timeout=120)
        result['repair_origin'] = {'patched': patched}
        return result

    def _wait_job(self, job, timeout=60):
        if not isinstance(job, dict) or 'job_id' not in job:
            return job
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            state = self.visible.request('job', job_id=job['job_id'])
            if state.get('status') == 'completed':
                return state.get('result')
            if state.get('status') == 'failed':
                raise RuntimeError(state.get('error') or 'native job failed')
            time.sleep(0.2)
        raise RuntimeError('native job still pending: ' + str(job.get('job_id')))

    def build_start(self):
        state=self.visible.read('state')
        if not state.get('project'):raise RuntimeError('No active project; wait for native state before starting a build')
        return self.builds.start(state['project'])

    def build_status(self, build_id):
        return self.builds.status(build_id)

    def build_diagnostics(self):
        return self.visible.read('build_diagnostics')

    def canvas_configure_bit_switch(self, **args):
        return self.visible.request('drawing_configure_bit_switch', **args)

    def build_cancel(self, build_id):
        return self.builds.cancel(build_id)

    def pages_list(self):
        return self.visible.read('pages_list')

    def page_create(self, **args):
        return self.visible.request('page_create', **args)

    def page_open(self, uuid):
        return self.visible.request('page_open', uuid=uuid)

    def page_transmission_add(self, **args):
        return self.visible.request('page_transmission_add', **args)

    def sampling_add(self, **args):
        return self.visible.request('sampling_add', **args)

    def trend_bind_sampling(self, **args):
        return self.visible.request('drawing_configure_trend', **args)

    def disk_bind_sampling(self, **args):
        return self.visible.request('drawing_configure_trend', **args)

    def page_transmission_remove(self, ref):
        return self.visible.request('page_transmission_remove', ref=ref)

    def page_timer_add(self, **args):
        return self.visible.request('page_timer_add', **args)

    def page_timer_remove(self, ref):
        return self.visible.request('page_timer_remove', ref=ref)

    def page_rename(self, uuid, name):
        return self.visible.request('page_rename', uuid=uuid, name=name)

    def page_copy(self, uuid, name):
        return self.visible.request('page_copy', uuid=uuid, name=name)

    def page_delete(self, uuid):
        return self.visible.request('page_delete', uuid=uuid)

    def visible_commands(self, query=''):
        result = self.visible.read('commands')
        if 'commands' in result:
            result['commands'] = [c for c in result['commands'] if query.lower() in c['command'].lower()]
        return result

    def visible_open_project(self, project):
        """验证工程目录可被原生宿主打开，避免超长路径产生模态提示。"""
        folder = self.project(project)
        self.check_native_project_directory(folder)
        return self.visible.request('open_project',file=str(next(folder.glob('*.fsprj'))))

    def visible_run_command(self, command):
        raise RuntimeError('BACKGROUND_ONLY: Native command dispatch is disabled. Use dedicated background build, page, canvas and model APIs.')

    def visible_save(self):
        return self.visible.request('save_project')

    def visible_job(self, job_id):
        return self.visible.request('job',job_id=job_id)

    def clone_widget(self, project, page_id, source_file, widget_id, name):
        folder = self.project(project)
        with self.lock(folder):
            source = self.document_path(folder,source_file)
            if source.suffix.lower() != '.wcfg': raise ValueError('Source must be a page document')
            path = self.find_page(folder,page_id); before = path.read_bytes()
            stage = self.inside(path.with_suffix('.'+uuid.uuid4().hex+'.mcp-staging'))
            try:
                result = self.native.call('clone_widget',source=str(source),input=str(path),output=str(stage),widget_id=widget_id,name=name)
                if path.read_bytes() != before: raise RuntimeError('Page changed outside MCP; edit not committed')
                backups = self.inside(folder/'.mcp-backups'); backups.mkdir(exist_ok=True)
                backup = backups/(path.name+'.'+uuid.uuid4().hex); backup.write_bytes(before)
                os.replace(stage,path)
                result.update(file=str(path),backup=str(backup),native_roundtrip_verified=True)
                return result
            finally: stage.unlink(missing_ok=True)

    def type_schema(self, **args):
        return self.native.call('type_schema', **args)

    def document_path(self, folder, file):
        relative = Path(file)
        if relative.is_absolute() or any(x.startswith('.') for x in relative.parts):
            raise ValueError('Expected project-relative document path without hidden directories')
        path = self.inside(folder / relative)
        if not path.is_relative_to(folder) or path.suffix.lower() not in ('.cfg','.wcfg','.fsprj','.fsvg') or not path.is_file():
            raise ValueError('Unsupported project document')
        if path.stat().st_size > 8*1024*1024: raise ValueError('Document exceeds 8 MiB limit')
        return path

    def list_documents(self, project):
        folder = self.project(project); documents = []
        for candidate in sorted(folder.rglob('*')):
            relative = candidate.relative_to(folder)
            if any(x.startswith('.') for x in relative.parts) or candidate.suffix.lower() not in ('.cfg','.wcfg','.fsprj','.fsvg'): continue
            path = self.document_path(folder, str(relative))
            row = {'file':relative.as_posix(), 'bytes':path.stat().st_size}
            try: row.update(self.native.call('document', input=str(path)))
            except RuntimeError as e: row.update(native_deserialization_verified=False, reason=str(e))
            documents.append(row)
        return {'documents':documents}

    def read_document(self, project, file):
        path = self.document_path(self.project(project),file)
        result = self.native.call('document',input=str(path)); content = path.read_bytes()
        result.update(file=file,sha256=hashlib.sha256(content).hexdigest(),xml=content.decode('utf-8-sig'))
        return result

    @staticmethod
    def xml_leaf(root, path):
        current = root
        for part in path.split('/'):
            match = re.fullmatch(r'([A-Za-z_][A-Za-z0-9_.-]*)(?:\[([1-9][0-9]*)\])?',part)
            if not match: raise ValueError('Invalid XML path segment: '+part)
            children = [e for e in current if e.tag.rsplit('}',1)[-1] == match[1]]
            if match[2]:
                index = int(match[2])-1
                if index >= len(children): raise ValueError('XML index outside existing siblings: '+path)
                current = children[index]
            else:
                if len(children) != 1: raise ValueError('XML path must identify one element: '+path)
                current = children[0]
        if len(current) or current.attrib: raise ValueError('Only existing plain leaf values may be patched')
        return current

    def patch_document(self, project, file, expected_sha256, changes):
        folder = self.project(project); path = self.document_path(folder,file)
        with self.lock(folder):
            before = path.read_bytes()
            if hashlib.sha256(before).hexdigest() != expected_sha256: raise ValueError('Stale document hash; reread before editing')
            # minidom preserves namespace declarations used inside xsi:type values.
            from xml.dom import minidom
            if b'<!DOCTYPE' in before.upper() or b'<!ENTITY' in before.upper(): raise ValueError('DTD/entity not supported')
            root = ET.fromstring(before); targets = set()
            dom = minidom.parseString(before)
            for change in changes:
                leaf = self.xml_leaf(root,change['path'])
                if id(leaf) in targets: raise ValueError('Duplicate target in patch')
                targets.add(id(leaf))
                node = dom.documentElement
                for part in change['path'].split('/'):
                    match = re.fullmatch(r'([A-Za-z_][A-Za-z0-9_.-]*)(?:\[([1-9][0-9]*)\])?',part)
                    children = [n for n in node.childNodes if n.nodeType == n.ELEMENT_NODE and n.localName == match[1]]
                    node = children[int(match[2] or '1')-1]
                while node.firstChild: node.removeChild(node.firstChild)
                node.appendChild(dom.createTextNode(change['value']))
            stage = self.inside(path.with_suffix('.'+uuid.uuid4().hex+'.mcp-staging'))
            output = self.inside(stage.with_suffix('.native-staging'))
            try:
                stage.write_bytes(dom.toxml(encoding='utf-8'))
                result = self.native.call('document',input=str(stage),output=str(output))
                restored = ET.parse(output).getroot()
                for change in changes:
                    if (self.xml_leaf(restored,change['path']).text or '') != change['value']:
                        raise ValueError('Native serialization normalized or rejected edited value: '+change['path'])
                if path.read_bytes() != before: raise RuntimeError('Document changed outside MCP; edit not committed')
                backups = self.inside(folder/'.mcp-backups'); backups.mkdir(exist_ok=True)
                backup = backups/(path.name+'.'+uuid.uuid4().hex); backup.write_bytes(before)
                # Keep untouched XML exactly as supplied; native serialization is the verification copy.
                os.replace(stage,path)
                result.update(file=file,backup=str(backup),sha256=hashlib.sha256(path.read_bytes()).hexdigest(),edited_values_verified=True,compiler='not_run')
                return result
            finally:
                stage.unlink(missing_ok=True); output.unlink(missing_ok=True)

    def capabilities(self):
        """列出实现入口与限定版本的验证基线；元数据声明不代表原生运行通过。"""
        commands = []
        for file in sorted((self.bin.parent/'Addins').rglob('*.addin')):
            try: root = ET.parse(file).getroot()
            except ET.ParseError: continue
            for element in root.iter():
                attrs = element.attrib
                if any(k.lower() in ('command','class','commandid') for k in attrs):
                    commands.append({'manifest':str(file.relative_to(self.bin.parent)),'element':element.tag,'attributes':dict(attrs)})
        return {'mcp_tools':[t['name'] for t in TOOLS], 'native_types':self.catalog(limit=1)['total'],
                'installed_declarations':commands, 'declarations_status':'metadata_only_not_callable',
                'coverage':{'dto_read_and_leaf_patch':'implemented_native_roundtrip','convenience_widget_creation':['text','rectangle','numeric_display'],
                'convenience_widget_gui_compatibility':'not_verified_for_all_models',
                'native_widget_clone':'existing_same_project_widgets_excluding_groups',
                'native_model_access':'in_process_inspect_get_set_call_factory',
                'native_canvas_pipeline':'static_text_prepare_edit_commit_save_reopen_real_mcp_verified',
                'native_canvas_ui_behavior':'no_property_dialog_no_mouse_input_foreground_preserved',
                'native_project_creation':'host_HMIProjectCreator_F010_verified',
                'native_basic_pages':'F010_list_create_open_rename_copy_delete_host_verified',
                'native_model_batch_edit':'page_geometry_native_undo_redo_save_reopen_prevalidation_and_UI_guards_host_verified',
                'compile':'background_BuildAsync_F010_native_compiler_success_no_dialog_foreground_preserved_verified',
                'compile_cancel':'cancellation_and_owned_process_cleanup_verified',
                'simulation':'background_api_not_implemented_end_to_end_not_verified',
                'download':'native_command_discovery_only','hardware':'not_verified'},
                'validation_baseline':{'fstudio_version':'3.0.15685.0','model':'F010',
                'scope':'representative_configurations_not_all_modes_or_hardware_runtime',
                'drawing_tools_exercised':52,'drawing_tools_compile_passed':42,
                'native_compiler_rejected':['CameraTool','DataTransmissionTool','PipeTool','SliderTool','StopWatchTool','StreamingMediaTool','TimePieceTool','TimerTool'],
                'guarded_native_deserialization_failure':['WindowSelectorTool'],
                'not_passed':['WebCameraTool']},
                'execution_policy':{'default':'background_native_model_operations_visible_canvas_drawing',
                'visible_prefix':'compatibility_names_for_background_host',
                'legacy_native_commands':'disabled_BACKGROUND_ONLY_no_dispatch',
                'offline_edits':'close_target_project_before_modifying_files'}}

    def call(self, name, args):
        if name == 'fstudio_health':
            return dict(self.native.call('health'), workspace=str(self.workspace), supported_widget_kinds=['text','rectangle','numeric_display'])
        return getattr(self, name.removeprefix('fstudio_'))(**args)


def serve(service):
    """按顺序处理 MCP 请求并公布批量路径用法；仅分发通过 schema 校验的工具参数。"""
    initialized = False; ready = False
    def send(obj):
        sys.stdout.write(json.dumps(obj, ensure_ascii=False, allow_nan=False, separators=(',', ':'))+'\n'); sys.stdout.flush()
    for raw in sys.stdin:
        request_id = None
        try:
            try: message = json.loads(raw, parse_constant=lambda x: (_ for _ in ()).throw(ValueError(x)))
            except (json.JSONDecodeError, ValueError):
                send({'jsonrpc':'2.0','id':None,'error':{'code':-32700,'message':'Parse error'}}); continue
            if not isinstance(message, dict) or message.get('jsonrpc') != '2.0' or not isinstance(message.get('method'), str):
                send({'jsonrpc':'2.0','id':None,'error':{'code':-32600,'message':'Invalid request'}}); continue
            method = message['method']; request_id = message.get('id')
            if 'id' not in message:
                if method == 'notifications/initialized' and initialized: ready = True
                continue
            if type(request_id) not in (str, int):
                send({'jsonrpc':'2.0','id':None,'error':{'code':-32600,'message':'Invalid request ID'}}); continue
            params = message.get('params', {})
            if not isinstance(params, dict): raise ValueError('params must be an object')
            if method == 'initialize':
                if initialized: raise ValueError('Already initialized')
                version = params.get('protocolVersion')
                if not isinstance(version, str): raise ValueError('protocolVersion required')
                initialized = True
                result = {'protocolVersion': version if version in SUPPORTED_PROTOCOLS else SUPPORTED_PROTOCOLS[0],
                          'capabilities': {'tools': {'listChanged':False}},
                          'serverInfo': {'name':'fstudio-dll-mcp','version':'0.3.0'},
                          'instructions':'FStudio background native-model operations, no property dialogs or mouse dragging. Start existing-page edits with canvas_snapshot to resolve unique Comment names and actual bounds; it excludes inherited common-page composition and nested children. Fetch every next_offset for a full page audit. For known controls use canvas_create_many with reusable presets; inspect bounds_adjusted. For discovery use canvas_prepare, model_get_many for additional known dotted paths, model_set_many, then canvas_commit or canvas_discard. Apply identical properties to same-page refs with model_apply_many; use model_set_many for distinct values. Avoid one model_get per intermediate object. Await the original job_id; never resubmit timed-out writes. Use pages_list and page_* for non-reserved Basic pages. Save before build_start, then build_status and compiler_success. visible_run_command is disabled with BACKGROUND_ONLY. Generic model methods are host-filtered. Snapshot/roundtrip is not visual, compilation or hardware verification. Close the target project only before offline DTO/file edits; keep it open for native operations.'}
            elif method == 'ping': result = {}
            elif not ready: raise ValueError('Initialize and send notifications/initialized first')
            elif method == 'tools/list': result = {'tools': TOOLS}
            elif method == 'tools/call':
                name = params.get('name'); args = params.get('arguments', {})
                if not isinstance(name, str) or name not in BY_NAME: raise ValueError('Unknown tool')
                validate(args, BY_NAME[name]['inputSchema'])
                try:
                    data = service.call(name, args)
                    result = {'content':[{'type':'text','text':json.dumps(data, ensure_ascii=False, allow_nan=False)}], 'structuredContent':data, 'isError':False}
                except Exception as e:
                    result = {'content':[{'type':'text','text':str(e)}], 'isError':True}
            else:
                send({'jsonrpc':'2.0','id':request_id,'error':{'code':-32601,'message':'Method not found'}}); continue
            send({'jsonrpc':'2.0','id':request_id,'result':result})
        except ValueError as e: send({'jsonrpc':'2.0','id':request_id,'error':{'code':-32602,'message':str(e)}})
        except Exception as e:
            print(type(e).__name__+': '+str(e), file=sys.stderr)
            send({'jsonrpc':'2.0','id':request_id,'error':{'code':-32603,'message':'Internal error'}})


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--workspace', type=Path, default=HERE/'projects')
    parser.add_argument('--fstudio-bin', type=Path, default=DEFAULT_BIN)
    args = parser.parse_args()
    if hasattr(sys.stdin, 'reconfigure'): sys.stdin.reconfigure(encoding='utf-8')
    if hasattr(sys.stdout, 'reconfigure'): sys.stdout.reconfigure(encoding='utf-8')
    service = FStudio(args.workspace, args.fstudio_bin)
    try: serve(service)
    finally: service.native.close()


if __name__ == '__main__': main()
