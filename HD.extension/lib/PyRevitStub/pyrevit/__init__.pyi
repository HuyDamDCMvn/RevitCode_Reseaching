"""pyRevit module stubs for IDE support."""
from typing import Any, Optional, List, Dict, Callable

# HOST_APP - provides access to Revit application info
class _HostApp:
    version: int
    build: str
    username: str
    is_newer_than: Callable[[int], bool]
    is_older_than: Callable[[int], bool]
    is_exactly: Callable[[int], bool]
    app: Any  # Autodesk.Revit.ApplicationServices.Application
    uiapp: Any  # Autodesk.Revit.UI.UIApplication
    doc: Any  # Autodesk.Revit.DB.Document
    uidoc: Any  # Autodesk.Revit.UI.UIDocument
    active_view: Any  # Autodesk.Revit.DB.View

HOST_APP: _HostApp

# EXEC_PARAMS - execution parameters
class _ExecParams:
    command_path: Optional[str]
    command_name: str
    command_bundle: str
    command_extension: str
    command_uniqueid: str
    command_class: Any
    engine_mgr: Any
    engine_id: str
    script_runtime: Any
    script_runtime_configs: Any
    exec_id: str
    exec_timestamp: str
    needs_clean_engine: bool
    needs_fullframe_engine: bool
    needs_persistent_engine: bool
    refresh_engine: bool
    engine_cfgs: Any
    debug_mode: bool
    doc_mode: bool
    config_mode: bool
    forced_debug_mode: bool
    event_args: Any
    command_mode: int

EXEC_PARAMS: _ExecParams

# script module
class script:
    @staticmethod
    def get_output() -> 'PyRevitOutput': ...
    
    @staticmethod
    def get_config(section: str = ...) -> Any: ...
    
    @staticmethod
    def get_universal_config() -> Any: ...
    
    @staticmethod
    def save_config() -> None: ...
    
    @staticmethod
    def get_instance_data_file(file_id: str) -> str: ...
    
    @staticmethod
    def get_data_file(file_id: str, file_ext: str = ...) -> str: ...
    
    @staticmethod
    def get_document_data_file(file_id: str, file_ext: str = ...) -> str: ...
    
    @staticmethod
    def exit() -> None: ...
    
    @staticmethod
    def get_all_buttons() -> List[Any]: ...
    
    @staticmethod
    def get_button(name: str) -> Any: ...
    
    @staticmethod
    def toggle_icon(state: bool) -> None: ...
    
    @staticmethod
    def get_envvar(varname: str) -> Optional[str]: ...
    
    @staticmethod
    def set_envvar(varname: str, value: str) -> None: ...
    
    @staticmethod
    def journal_write(data_key: str, msg: str) -> None: ...
    
    @staticmethod  
    def journal_read(data_key: str) -> Optional[str]: ...

class PyRevitOutput:
    def print_md(self, md_str: str) -> None: ...
    def print_html(self, html_str: str) -> None: ...
    def print_code(self, code_str: str) -> None: ...
    def print_table(self, table_data: List[List[Any]], columns: List[str] = ...) -> None: ...
    def linkify(self, element_id: Any, title: str = ...) -> str: ...
    def log_debug(self, msg: str) -> None: ...
    def log_success(self, msg: str) -> None: ...
    def log_warning(self, msg: str) -> None: ...
    def log_error(self, msg: str) -> None: ...
    def set_title(self, title: str) -> None: ...
    def set_width(self, width: int) -> None: ...
    def set_height(self, height: int) -> None: ...
    def close_others(self, all_open_outputs: bool = ...) -> None: ...
    def close(self) -> None: ...
    def show(self) -> None: ...
    def hide(self) -> None: ...
    def lock_size(self) -> None: ...
    def unlock_size(self) -> None: ...
    def freeze(self) -> None: ...
    def unfreeze(self) -> None: ...
    def save_contents(self, filepath: str) -> None: ...
    def open_url(self, url: str) -> None: ...
    def open_page(self, filepath: str) -> None: ...
    def update_progress(self, cur_value: int, max_value: int) -> None: ...
    def reset_progress(self) -> None: ...
    def hide_progress(self) -> None: ...
    def indeterminate_progress(self, state: bool = ...) -> None: ...

# forms module  
class forms:
    @staticmethod
    def alert(
        msg: str, 
        title: str = ..., 
        sub_msg: str = ...,
        ok: bool = ...,
        cancel: bool = ...,
        yes: bool = ...,
        no: bool = ...,
        retry: bool = ...,
        exitscript: bool = ...,
        warn_icon: bool = ...,
        options: List[str] = ...
    ) -> Optional[bool]: ...
    
    @staticmethod
    def ask_for_string(
        default: str = ...,
        prompt: str = ...,
        title: str = ...,
        **kwargs: Any
    ) -> Optional[str]: ...
    
    @staticmethod
    def ask_for_one_item(
        items: List[Any],
        default: Any = ...,
        prompt: str = ...,
        title: str = ...
    ) -> Optional[Any]: ...
    
    @staticmethod
    def ask_for_date(
        default: Any = ...,
        prompt: str = ...,
        title: str = ...
    ) -> Optional[Any]: ...
    
    @staticmethod
    def select_views(
        title: str = ...,
        button_name: str = ...,
        width: int = ...,
        multiple: bool = ...,
        filterfunc: Callable = ...,
        doc: Any = ...
    ) -> Optional[List[Any]]: ...
    
    @staticmethod
    def select_sheets(
        title: str = ...,
        button_name: str = ...,
        width: int = ...,
        multiple: bool = ...,
        filterfunc: Callable = ...,
        doc: Any = ...
    ) -> Optional[List[Any]]: ...
    
    @staticmethod
    def select_levels(
        title: str = ...,
        button_name: str = ...,
        width: int = ...,
        multiple: bool = ...,
        filterfunc: Callable = ...,
        doc: Any = ...
    ) -> Optional[List[Any]]: ...
    
    @staticmethod
    def select_image(
        images: List[Any],
        title: str = ...,
        button_name: str = ...
    ) -> Optional[Any]: ...
    
    @staticmethod
    def select_swatch(
        title: str = ...,
        button_name: str = ...
    ) -> Optional[Any]: ...
    
    @staticmethod
    def CommandSwitchWindow(
        context: Any,
        switches: List[str] = ...,
        message: str = ...,
        recognize_access_key: bool = ...
    ) -> Optional[str]: ...
    
    @staticmethod
    def SelectFromList(
        context: Any,
        title: str = ...,
        button_name: str = ...,
        width: int = ...,
        height: int = ...,
        multiselect: bool = ...,
        filterfunc: Callable = ...
    ) -> Optional[List[Any]]: ...
    
    @staticmethod
    def WarningBar(message: str, title: str = ...) -> Any: ...
    
    @staticmethod
    def ProgressBar(
        title: str = ...,
        indeterminate: bool = ...,
        cancellable: bool = ...,
        step: int = ...
    ) -> Any: ...
    
    @staticmethod
    def toast(
        message: str,
        title: str = ...,
        appid: str = ...,
        icon: str = ...,
        click: str = ...,
        actions: Dict[str, str] = ...
    ) -> None: ...
    
    @staticmethod
    def check_workshared(doc: Any = ..., message: str = ...) -> bool: ...
    
    @staticmethod
    def check_selection(exitscript: bool = ...) -> bool: ...
    
    @staticmethod
    def check_familydoc(exitscript: bool = ..., doc: Any = ...) -> bool: ...
    
    @staticmethod
    def check_modeldoc(exitscript: bool = ..., doc: Any = ...) -> bool: ...
    
    @staticmethod
    def inform_wip() -> None: ...

# revit module
class revit:
    doc: Any  # Autodesk.Revit.DB.Document
    uidoc: Any  # Autodesk.Revit.UI.UIDocument
    app: Any  # Autodesk.Revit.ApplicationServices.Application
    uiapp: Any  # Autodesk.Revit.UI.UIApplication
    active_view: Any  # Autodesk.Revit.DB.View
    
    @staticmethod
    def get_selection() -> List[Any]: ...
    
    @staticmethod  
    def pick_element(message: str = ...) -> Any: ...
    
    @staticmethod
    def pick_elements(message: str = ...) -> List[Any]: ...
    
    @staticmethod
    def pick_element_by_category(category: Any, message: str = ...) -> Any: ...

# DB module (shortcut to Autodesk.Revit.DB)
class db:
    pass

# coreutils
class coreutils:
    @staticmethod
    def get_enum_values(enum_type: type) -> List[Any]: ...
    
    @staticmethod
    def cleanup_string(input_str: str) -> str: ...
    
    @staticmethod
    def get_file_name(file_path: str) -> str: ...
    
    @staticmethod
    def verify_directory(dir_path: str) -> bool: ...

# output module
class output:
    @staticmethod
    def get_output() -> PyRevitOutput: ...
