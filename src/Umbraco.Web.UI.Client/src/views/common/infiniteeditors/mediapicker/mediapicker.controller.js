//used for the media picker dialog
angular.module("umbraco")
    .controller("Umbraco.Editors.MediaPickerController",
        function ($scope, mediaResource, entityResource, userService, mediaHelper, mediaTypeHelper, eventsService, treeService, localStorageService, localizationService, editorService) {
            
            
            if (!$scope.model.title) {
                localizationService.localizeMany(["defaultdialogs_selectMedia", "general_includeFromsubFolders"])
                    .then(function (data) {
                        $scope.labels = {
                            title: data[0],
                            includeSubFolders: data[1]
                        }
                    });
            }

            var dialogOptions = $scope.model;
            
            $scope.disableFolderSelect = (dialogOptions.disableFolderSelect && dialogOptions.disableFolderSelect !== "0") ? true : false;
            $scope.onlyImages = (dialogOptions.onlyImages && dialogOptions.onlyImages !== "0") ? true : false;
            $scope.onlyFolders = (dialogOptions.onlyFolders && dialogOptions.onlyFolders !== "0") ? true : false;
            $scope.showDetails = (dialogOptions.showDetails && dialogOptions.showDetails !== "0") ? true : false;
            $scope.multiPicker = (dialogOptions.multiPicker && dialogOptions.multiPicker !== "0") ? true : false;
            $scope.startNodeId = dialogOptions.startNodeId ? dialogOptions.startNodeId : -1;
            $scope.cropSize = dialogOptions.cropSize;
            $scope.lastOpenedNode = localStorageService.get("umbLastOpenedMediaNodeId");
            $scope.lockedFolder = true;
            $scope.allowMediaEdit = dialogOptions.allowMediaEdit ? dialogOptions.allowMediaEdit : false;

            var userStartNodes = [];

            var umbracoSettings = Umbraco.Sys.ServerVariables.umbracoSettings;
            var allowedUploadFiles = mediaHelper.formatFileTypes(umbracoSettings.allowedUploadFiles);
            if ($scope.onlyImages) {
                $scope.acceptedFileTypes = mediaHelper.formatFileTypes(umbracoSettings.imageFileTypes);
            } else {
                // Use whitelist of allowed file types if provided
                if (allowedUploadFiles !== '') {
                    $scope.acceptedFileTypes = allowedUploadFiles;
                } else {
                    // If no whitelist, we pass in a blacklist by adding ! to the file extensions, allowing everything EXCEPT for disallowedUploadFiles
                    $scope.acceptedFileTypes = !mediaHelper.formatFileTypes(umbracoSettings.disallowedUploadFiles);
                }
            }

            $scope.maxFileSize = umbracoSettings.maxFileSize + "KB";

            $scope.model.selection = [];

            $scope.acceptedMediatypes = [];
            mediaTypeHelper.getAllowedImagetypes($scope.startNodeId)
                .then(function (types) {
                    $scope.acceptedMediatypes = types;
                });

            var dataTypeKey = null;
            if($scope.model && $scope.model.dataTypeKey) {
                dataTypeKey = $scope.model.dataTypeKey;
            }
            $scope.searchOptions = {
                pageNumber: 1,
                pageSize: 100,
                totalItems: 0,
                totalPages: 0,
                filter: '',
                dataTypeKey: dataTypeKey
            };

            //preload selected item
            $scope.target = undefined;
            if (dialogOptions.currentTarget) {
                $scope.target = dialogOptions.currentTarget;
            }

            function onInit() {
                userService.getCurrentUser().then(function (userData) {
                    userStartNodes = userData.startMediaIds;

                    if ($scope.startNodeId !== -1) {
                        entityResource.getById($scope.startNodeId, "media")
                            .then(function (ent) {
                                $scope.startNodeId = ent.id;
                                run();
                            });
                    } else {
                        run();
                    }
                });
            }

            function run() {
                //default root item
                if (!$scope.target) {
                    if ($scope.lastOpenedNode && $scope.lastOpenedNode !== -1) {
                        entityResource.getById($scope.lastOpenedNode, "media")
                            .then(ensureWithinStartNode, gotoStartNode);
                    } else {
                        gotoStartNode();
                    }
                } else {
                    //if a target is specified, go look it up - generally this target will just contain ids not the actual full
                    //media object so we need to look it up
                    var id = $scope.target.udi ? $scope.target.udi : $scope.target.id;
                    var altText = $scope.target.altText;
                    entityResource.getById(id, "Media")
                        .then(function (node) {
                            $scope.target = node;
                            if (ensureWithinStartNode(node)) {
                                selectImage(node);
                                $scope.target.url = mediaHelper.resolveFile(node);
                                $scope.target.altText = altText;
                                $scope.openDetailsDialog();
                            }
                        },
                            gotoStartNode);
                }
            }

            $scope.upload = function (v) {
                angular.element(".umb-file-dropzone .file-select").trigger("click");
            };

            $scope.dragLeave = function (el, event) {
                $scope.activeDrag = false;
            };

            $scope.dragEnter = function (el, event) {
                $scope.activeDrag = true;
            };

            $scope.submitFolder = function () {
                if ($scope.model.newFolderName) {
                    $scope.model.creatingFolder = true;
                    mediaResource
                        .addFolder($scope.model.newFolderName, $scope.currentFolder.id)
                        .then(function (data) {
                            //we've added a new folder so lets clear the tree cache for that specific item
                            treeService.clearCache({
                                cacheKey: "__media", //this is the main media tree cache key
                                childrenOf: data.parentId //clear the children of the parent
                            });
                            $scope.model.creatingFolder = false;
                            $scope.gotoFolder(data);
                            $scope.model.showFolderInput = false;
                            $scope.model.newFolderName = "";
                        });
                } else {
                    $scope.model.showFolderInput = false;
                }
            };

            $scope.enterSubmitFolder = function (event) {
                if (event.keyCode === 13) {
                    $scope.submitFolder();
                    event.stopPropagation();
                }
            };

            $scope.gotoFolder = function (folder) {
                if (!$scope.multiPicker) {
                    deselectAllImages($scope.model.selection);
                }

                if (!folder) {
                    folder = { id: -1, name: "Media", icon: "icon-folder" };
                }

                if (folder.id > 0) {
                    entityResource.getAncestors(folder.id, "media", null, { dataTypeKey: dataTypeKey })
                        .then(function (anc) {
                            $scope.path = _.filter(anc,
                                function (f) {
                                    return f.path.indexOf($scope.startNodeId) !== -1;
                                });
                        });

                    mediaTypeHelper.getAllowedImagetypes(folder.id)
                        .then(function (types) {
                            $scope.acceptedMediatypes = types;
                        });
                } else {
                    $scope.path = [];
                }

                $scope.lockedFolder = (folder.id === -1 && $scope.model.startNodeIsVirtual) || hasFolderAccess(folder) === false;

                $scope.currentFolder = folder;
                localStorageService.set("umbLastOpenedMediaNodeId", folder.id);
                return getChildren(folder.id);
            };

            $scope.clickHandler = function (image, event, index) {
                
                if (image.isFolder) {
                    if ($scope.disableFolderSelect) {
                        $scope.gotoFolder(image);
                    } else {
                        selectImage(image);
                    }
                } else {
                    if ($scope.showDetails) {
                        
                        $scope.target = image;
                        
                        // handle both entity and full media object
                        if (image.image) {
                            $scope.target.url = image.image;
                        } else {
                            $scope.target.url = mediaHelper.resolveFile(image);
                        }
                        
                        $scope.openDetailsDialog();
                    } else {
                        selectImage(image);
                    }
                }
            };

            $scope.clickItemName = function (item) {
                if (item.isFolder) {
                    $scope.gotoFolder(item);
                }
            };

            function selectImage(image) {
                if(!image.selectable) {
                    return;
                }
                if (image.selected) {
                    for (var i = 0; $scope.model.selection.length > i; i++) {
                        var imageInSelection = $scope.model.selection[i];
                        if (image.key === imageInSelection.key) {
                            image.selected = false;
                            $scope.model.selection.splice(i, 1);
                        }
                    }
                } else {
                    if (!$scope.multiPicker) {
                        deselectAllImages($scope.model.selection);
                    }
                    eventsService.emit("dialogs.mediaPicker.select", image);
                    image.selected = true;
                    $scope.model.selection.push(image);
                }
            }

            function deselectAllImages(images) {
                for (var i = 0; i < images.length; i++) {
                    var image = images[i];
                    image.selected = false;
                }
                images.length = 0;
            }

            $scope.onUploadComplete = function (files) {
                $scope.gotoFolder($scope.currentFolder).then(function () {
                    if (files.length === 1 && $scope.model.selection.length === 0) {
                        var image = $scope.images[$scope.images.length - 1];
                        $scope.target = image;
                        $scope.target.url = mediaHelper.resolveFile(image);
                        selectImage(image);
                    }
                });
            };

            $scope.onFilesQueue = function () {
                $scope.activeDrag = false;
            };

            function ensureWithinStartNode(node) {
                // make sure that last opened node is on the same path as start node
                var nodePath = node.path.split(",");

                // also make sure the node is not trashed
                if (nodePath.indexOf($scope.startNodeId.toString()) !== -1 && node.trashed === false) {
                    $scope.gotoFolder({ id: $scope.lastOpenedNode, name: "Media", icon: "icon-folder", path: node.path });
                    return true;
                } else {
                    $scope.gotoFolder({ id: $scope.startNodeId, name: "Media", icon: "icon-folder" });
                    return false;
                }
            }

            function hasFolderAccess(node) {
                var nodePath = node.path ? node.path.split(',') : [node.id];

                for (var i = 0; i < nodePath.length; i++) {
                    if (userStartNodes.indexOf(parseInt(nodePath[i])) !== -1)
                        return true;
                }

                return false;
            }

            function gotoStartNode(err) {
                $scope.gotoFolder({ id: $scope.startNodeId, name: "Media", icon: "icon-folder" });
            }

            $scope.openDetailsDialog = function () {

                $scope.mediaPickerDetailsOverlay = {};
                $scope.mediaPickerDetailsOverlay.show = true;

                $scope.mediaPickerDetailsOverlay.submit = function (model) {
                    $scope.model.selection.push($scope.target);
                    $scope.model.submit($scope.model);

                    $scope.mediaPickerDetailsOverlay.show = false;
                    $scope.mediaPickerDetailsOverlay = null;
                };

                $scope.mediaPickerDetailsOverlay.close = function (oldModel) {
                    $scope.mediaPickerDetailsOverlay.show = false;
                    $scope.mediaPickerDetailsOverlay = null;
                };
            };

            var debounceSearchMedia = _.debounce(function () {
                $scope.$apply(function () {
                    if ($scope.searchOptions.filter) {
                        searchMedia();
                    } else {
                        
                        // reset pagination
                        $scope.searchOptions = {
                            pageNumber: 1,
                            pageSize: 100,
                            totalItems: 0,
                            totalPages: 0,
                            filter: '',
                            dataTypeKey: dataTypeKey
                        };
                        getChildren($scope.currentFolder.id);
                    }
                });
            }, 500);

            $scope.changeSearch = function () {
                $scope.loading = true;
                debounceSearchMedia();
            };

            $scope.toggle = function () {
                // Make sure to activate the changeSearch function everytime the toggle is clicked
                $scope.changeSearch();
            }

            $scope.changePagination = function (pageNumber) {
                $scope.loading = true;
                $scope.searchOptions.pageNumber = pageNumber;
                searchMedia();
            };

            function searchMedia() {
                $scope.loading = true;
                entityResource.getPagedDescendants($scope.currentFolder.id, "Media", $scope.searchOptions)
                    .then(function (data) {
                        // update image data to work with image grid
                        angular.forEach(data.items, function (mediaItem) {
                            setMediaMetaData(mediaItem);
                        });
                        // update images
                        $scope.images = data.items ? data.items : [];
                        // update pagination
                        if (data.pageNumber > 0)
                            $scope.searchOptions.pageNumber = data.pageNumber;
                        if (data.pageSize > 0)
                            $scope.searchOptions.pageSize = data.pageSize;
                        $scope.searchOptions.totalItems = data.totalItems;
                        $scope.searchOptions.totalPages = data.totalPages;
                        // set already selected images to selected
                        preSelectImages();
                        $scope.loading = false;
                    });
            }

            function setMediaMetaData(mediaItem) {
                // set thumbnail and src
                mediaItem.thumbnail = mediaHelper.resolveFileFromEntity(mediaItem, true);
                mediaItem.image = mediaHelper.resolveFileFromEntity(mediaItem, false);
                // set properties to match a media object
                if (mediaItem.metaData) {
                    mediaItem.properties = [];
                    if (mediaItem.metaData.umbracoWidth && mediaItem.metaData.umbracoHeight) {
                        mediaItem.properties.push(
                            {
                                alias: "umbracoWidth",
                                editor: mediaItem.metaData.umbracoWidth.PropertyEditorAlias,
                                value: mediaItem.metaData.umbracoWidth.Value
                            },
                            {
                                alias: "umbracoHeight",
                                editor: mediaItem.metaData.umbracoHeight.PropertyEditorAlias,
                                value: mediaItem.metaData.umbracoHeight.Value
                            }
                        );
                    }
                    if (mediaItem.metaData.umbracoFile) {
                        // this is required for resolving files through the mediahelper
                        mediaItem.properties.push(
                            {
                                alias: "umbracoFile",
                                editor: mediaItem.metaData.umbracoFile.PropertyEditorAlias,
                                value: mediaItem.metaData.umbracoFile.Value
                            }
                        );
                    }
                }
            }

            function getChildren(id) {
                $scope.loading = true;
                return entityResource.getChildren(id, "Media", $scope.searchOptions)
                    .then(function (data) {
                        for (var i = 0; i < data.length; i++) {
                            if (data[i].metaData.MediaPath !== null) {
                                data[i].thumbnail = mediaHelper.resolveFileFromEntity(data[i], true);
                                data[i].image = mediaHelper.resolveFileFromEntity(data[i], false);
                            }
                        }
                        $scope.searchOptions.filter = "";
                        $scope.images = data ? data : [];
                        // set already selected images to selected
                        preSelectImages();
                        $scope.loading = false;
                    });
            }

            function preSelectImages() {
                for (var folderImageIndex = 0; folderImageIndex < $scope.images.length; folderImageIndex++) {
                    var folderImage = $scope.images[folderImageIndex];
                    var imageIsSelected = false;

                    if ($scope.model && angular.isArray($scope.model.selection)) {
                        for (var selectedImageIndex = 0;
                            selectedImageIndex < $scope.model.selection.length;
                            selectedImageIndex++) {
                            var selectedImage = $scope.model.selection[selectedImageIndex];

                            if (folderImage.key === selectedImage.key) {
                                imageIsSelected = true;
                            }
                        }
                    }

                    if (imageIsSelected) {
                        folderImage.selected = true;
                    }
                }
            }

            $scope.editMediaItem = function (item) {
                var mediaEditor = {
                    id: item.id,
                    submit: function (model) {
                        editorService.close()
                        // update the media picker item in the picker so it matched the saved media item
                        // the media picker is using media entities so we get the 
                        // entity so we easily can format it for use in the media grid
                        if (model && model.mediaNode) {
                            entityResource.getById(model.mediaNode.id, "media")
                                .then(function (mediaEntity) {
                                    angular.extend(item, mediaEntity);
                                    setMediaMetaData(item);
                                    setUpdatedMediaNodes(item);
                                });
                        }
                    },
                    close: function (model) {
                        setUpdatedMediaNodes(item);
                        editorService.close();
                    }
                };
                editorService.mediaEditor(mediaEditor);
            };

            function setUpdatedMediaNodes(item) {
                // add udi to list of updated media items so we easily can update them in other editors
                if ($scope.model.updatedMediaNodes.indexOf(item.udi) === -1) {
                    $scope.model.updatedMediaNodes.push(item.udi);
                }
            }

            $scope.submit = function () {
                if ($scope.model.submit) {
                    $scope.model.submit($scope.model);
                }
            };

            $scope.close = function () {
                if ($scope.model.close) {
                    $scope.model.close($scope.model);
                }
            };

            onInit();

        });
