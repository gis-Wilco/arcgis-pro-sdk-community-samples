﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Data;
using ArcGIS.Core.Events;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Catalog;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Editing;
using ArcGIS.Desktop.Editing.Attributes;
using ArcGIS.Desktop.Editing.Events;
using ArcGIS.Desktop.Extensions;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;
using ArcGIS.Desktop.Framework.Dialogs;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using ArcGIS.Desktop.Mapping.Events;

namespace ModifyNewlyAddedFeatures
{
  internal class ModifyMonitorViewModel : DockPane
  {
    private const string _dockPaneID = "ModifyNewlyAddedFeatures_ModifyMonitor";
    private string _txtStatus;
    private string _polygonLayerName = "TestPolygons";
    private string _pointLayerName = "TestPoints";
    private bool _isStoreInOnRowEventEnabled = false;
    private bool _isModificationEnabled = false;
    private SubscriptionToken _activeMapViewChangedEvent = null;
    private SubscriptionToken _onRowChangedEvent = null;
    private SubscriptionToken _onRowCreatedEvent = null;
    private FeatureLayer _workedOnPolygonLayer = null;

    protected ModifyMonitorViewModel() { }

    private void ActivateModification()
    {
      DeActivateModification();
      UpdateStatusText($@"Activate Modification - MapView active: {(MapView.Active != null)}");
      var activeMapView = MapView.Active;
      if (activeMapView == null)
      {
        // the map view is not active yet
        _activeMapViewChangedEvent = ActiveMapViewChangedEvent.Subscribe(OnActiveMapViewChangedEvent);
        return;
      }
      SetUpRowEventListener(activeMapView, PolygonLayerName);
    }

    private void DeActivateModification()
    {
      if (_activeMapViewChangedEvent != null || _onRowChangedEvent != null || _onRowCreatedEvent != null)
      {
        UpdateStatusText($@"DeActivate Modification");
        if (_activeMapViewChangedEvent != null) ActiveMapViewChangedEvent.Unsubscribe(_activeMapViewChangedEvent);
        if (_onRowChangedEvent != null) RowChangedEvent.Unsubscribe(_onRowChangedEvent);
        if (_onRowCreatedEvent != null) RowChangedEvent.Unsubscribe(_onRowCreatedEvent);
        _onRowChangedEvent = null;
        _onRowCreatedEvent = null;
        _activeMapViewChangedEvent = null;
      }
    }

    /// <summary>
    /// setup event listeners for a given featurelayer (with featurelayer name)
    /// </summary>
    /// <param name="activeMapView"></param>
    /// <param name="featureLayerName"></param>
    private void SetUpRowEventListener(MapView activeMapView, string featureLayerName)
    {
      // Find our polygon feature layer
      _workedOnPolygonLayer = activeMapView.Map.GetLayersAsFlattenedList().FirstOrDefault((fl) => fl.Name == featureLayerName) as FeatureLayer;
      UpdateStatusText((_workedOnPolygonLayer == null)
                  ? $@"{featureLayerName} NOT found"
                  : $@"Listening to {featureLayerName} changes");
      // setup event listening ...
      QueuedTask.Run(() =>
      {
        _onRowChangedEvent = RowChangedEvent.Subscribe(OnRowChangedEvent, _workedOnPolygonLayer.GetTable());
        _onRowCreatedEvent = RowCreatedEvent.Subscribe(OnRowCreatedEvent, _workedOnPolygonLayer.GetTable());
      });
    }

    /// <summary>
    /// Called for each row change
    /// </summary>
    /// <param name="args"></param>
    private void OnRowChangedEvent(RowChangedEventArgs args)
    {
      UpdateStatusText($@"Row changed: {args.EditType}");
      UpdateRowIfNeeded(args);
    }

    /// <summary>
    /// called for each newly created row
    /// </summary>
    /// <param name="args"></param>
    private void OnRowCreatedEvent(RowChangedEventArgs args)
    {
      UpdateStatusText($@"Row created: {args.EditType}");
      UpdateRowIfNeeded(args);
    }

    private Guid _currentRowChangedGuid = Guid.Empty;

    private void UpdateRowIfNeeded(RowChangedEventArgs args)
    {
      // From the ProConcept documentation @https://github.com/esri/arcgis-pro-sdk/wiki/ProConcepts-Editing#row-events
      // If you need to edit additional tables within the RowEvent you must use the 
      // ArcGIS.Core.Data API to edit the tables directly. 
      // Do not use a new edit operation to create or modify features or rows in your 
      // RowEvent callbacks. 
      // RowEvent callbacks are always called on the QueuedTask so there is no need 
      // to wrap your code within a QueuedTask.Run lambda.
      try
      {
        // prevent re-entrance (only if row.Store() is called)
        if (_isStoreInOnRowEventEnabled
            && _currentRowChangedGuid == args.Guid)
        {
          UpdateStatusText($@"Re-entrant call - ignored");
          return;
        }

        var row = args.Row;
        var rowDefinition = (row.GetTable() as FeatureClass).GetDefinition();
        var geom = row[rowDefinition.GetShapeField()] as Geometry;
        MapPoint pntLogging = MapView.Active.Extent.Center;

        var rowCursorOverlayPoly = _workedOnPolygonLayer.Search(geom, SpatialRelationship.Intersects);
        Geometry geomOverlap = null;
        while (rowCursorOverlayPoly.MoveNext())
        {
          var feature = rowCursorOverlayPoly.Current as Feature;
          // exclude the search polygon
          if (row.GetObjectID() == feature.GetObjectID()) continue;

          var geomOverlayPoly = feature.GetShape();
          if (geomOverlayPoly == null) continue;
          if (geomOverlap == null)
          {
            geomOverlap = geomOverlayPoly.Clone();
            continue;
          }
          geomOverlap = GeometryEngine.Instance.Union(geomOverlap, geomOverlayPoly);
        }
        var description = string.Empty;
        if (!geomOverlap.IsNullOrEmpty())
        {
          var correctedGeom = GeometryEngine.Instance.Difference(geom, geomOverlap);
          row["Shape"] = correctedGeom;
          if (!correctedGeom.IsNullOrEmpty()) {
            // use the centerpoint of the polygon as the point for the logging entry
            pntLogging = GeometryEngine.Instance.LabelPoint(correctedGeom);
              }
          description = correctedGeom.IsEmpty ? "Polygon can't be inside existing polygon" : "Corrected input polygon";
        }
        else
          description = "No overlapping polygons found";
        row["Description"] = description;
        UpdateStatusText($@"Row: {description}");
        if (_isStoreInOnRowEventEnabled)
        {
          // calling store will result in a recursive row changed event as long as any
          // attribute columns have changed
          // In this case i would need to look at args.Guid to prevent re-entrance
          _currentRowChangedGuid = args.Guid;
          row.Store();
          _currentRowChangedGuid = Guid.Empty;
        }

        // update logging feature class with centerpoint of polygon
        var geoDatabase = new Geodatabase(new FileGeodatabaseConnectionPath(new Uri(Project.Current.DefaultGeodatabasePath)));
        var loggingFeatureClass = geoDatabase.OpenDataset<FeatureClass>(_pointLayerName);
        var loggingFCDefinition = loggingFeatureClass.GetDefinition();
        using (var rowbuff = loggingFeatureClass.CreateRowBuffer())
        {
          // needs a 3D point
          rowbuff[loggingFCDefinition.GetShapeField()] = MapPointBuilder.CreateMapPoint (pntLogging.X, pntLogging.Y, 0, pntLogging.SpatialReference);
          rowbuff["Description"] = "OID: " + row.GetObjectID().ToString() + " " + DateTime.Now.ToShortTimeString();
          loggingFeatureClass.CreateRow(rowbuff);
        }
      }
      catch (Exception e)
      {
        MessageBox.Show($@"Error in UpdateRowIfNeeded for OID: {args.Row.GetObjectID()} in {_workedOnPolygonLayer.Name}: {e.ToString()}");
      }
    }

    /// <summary>
    /// Waiting for the active map view to change in order to setup event listening
    /// </summary>
    /// <param name="args"></param>
    private void OnActiveMapViewChangedEvent(ActiveMapViewChangedEventArgs args)
    {
      if (args.IncomingView != null)
      {
        SetUpRowEventListener(args.IncomingView, PolygonLayerName);
        ActiveMapViewChangedEvent.Unsubscribe(_activeMapViewChangedEvent);
        _activeMapViewChangedEvent = null;
      }
    }

    /// <summary>
    /// Using RowChangedEventArgs returns inspector for changes (created) row
    /// </summary>
    /// <param name="args"></param>
    /// <returns></returns>
    private async Task<Inspector> GetInspectorForRow(RowChangedEventArgs args)
    {
      Inspector inspr = new Inspector();
      try
      {
        //Load the inspector 
        await inspr.LoadAsync(_workedOnPolygonLayer, args.Row.GetObjectID());
      }
      catch (Exception e)
      {
        MessageBox.Show($@"Unable to get Inspector for OID: {args.Row.GetObjectID()} in {_workedOnPolygonLayer.Name}: {e.ToString()}");
      }
      return inspr;
    }

    /// <summary>
    /// Update status text on GUI thread from non GUI thread
    /// </summary>
    /// <param name="text"></param>
    private void UpdateStatusText(string text)
    {
      if (System.Windows.Application.Current.Dispatcher.CheckAccess())
      {
        TxtStatus += text + Environment.NewLine;
      }
      else
      {
        ProApp.Current.Dispatcher.BeginInvoke(DispatcherPriority.Normal,
          (Action)(() =>
          {
            TxtStatus += text + Environment.NewLine;
          }));
      }
    }

    /// <summary>
    /// Name of the Feature Layer to work with
    /// </summary>
    public string TxtStatus
    {
      get { return _txtStatus; }
      set
      {
        SetProperty(ref _txtStatus, value, () => TxtStatus);
      }
    }

    /// <summary>
    /// Polygon Layer Name
    /// </summary>
    public string PolygonLayerName
    {
      get { return _polygonLayerName; }
      set
      {
        SetProperty(ref _polygonLayerName, value, () => PolygonLayerName);
      }
    }

    /// <summary>
    /// Point Layer Name
    /// </summary>
    public string PointLayerName
    {
      get { return _pointLayerName; }
      set
      {
        SetProperty(ref _pointLayerName, value, () => PointLayerName);
      }
    }

    public bool IsStoreInOnRowEventEnabled
    {
      get { return _isStoreInOnRowEventEnabled; }
      set
      {
        if (value == _isStoreInOnRowEventEnabled) return;
        SetProperty(ref _isStoreInOnRowEventEnabled, value, () => IsStoreInOnRowEventEnabled);
      }
    }

    public bool IsModificationEnabled
    {
      get { return _isModificationEnabled; }
      set
      {
        if (value == _isModificationEnabled) return;
        SetProperty(ref _isModificationEnabled, value, () => IsModificationEnabled);
        if (_isModificationEnabled) ActivateModification();
        else DeActivateModification();
      }
    }

    /// <summary>
    /// Show the DockPane.
    /// </summary>
    internal static void Show()
    {
      DockPane pane = FrameworkApplication.DockPaneManager.Find(_dockPaneID);
      if (pane == null)
        return;

      pane.Activate();
    }

    /// <summary>
    /// Text shown near the top of the DockPane.
    /// </summary>
    private string _heading = "Modify New Added Row Monitor";
    public string Heading
    {
      get { return _heading; }
      set
      {
        SetProperty(ref _heading, value, () => Heading);
      }
    }
  }

  /// <summary>
  /// Button implementation to show the DockPane.
  /// </summary>
	internal class ModifyMonitor_ShowButton : Button
  {
    protected override void OnClick()
    {
      ModifyMonitorViewModel.Show();
    }
  }
}
